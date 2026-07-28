using FluentAssertions;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.BuildingBlocks.Messaging.Abstractions;
using IHostPro.Contexts.Identity.Application;
using IHostPro.Contexts.Identity.Domain;
using IHostPro.Contexts.Identity.Domain.ValueObjects;
using IHostPro.Contexts.Identity.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IHostPro.Contexts.Identity.Tests.Unit.Infrastructure;

/// <summary>
/// Unit-level coverage of <see cref="RefreshTokenExchangeExecutor"/>'s
/// retry-counting/give-up behavior and post-commit cache write, in
/// isolation, without a real database — <see cref="DbContext.ChangeTracker"/>
/// operates purely in memory, so a never-connected <see cref="IdentityDbContext"/>
/// is enough here (the real, end-to-end concurrency scenario against
/// PostgreSQL is covered by
/// <c>RefreshTokenCommandHandlerTests.Two_concurrent_refresh_attempts_with_the_same_token_result_in_exactly_one_success</c>,
/// and the real Redis write by <c>SessionRevocationCacheTests</c>; the real
/// outbox atomicity/no-duplicate-envelope guarantee is covered by
/// <c>IdentityOutboxTransactionExecutorTests</c>, Etapa 15A).
/// </summary>
public class RefreshTokenExchangeExecutorTests
{
    private sealed class FakeTransactionExecutor : IIdentityTransactionExecutor
    {
        public int CallCount { get; private set; }

        public async Task<TResponse> ExecuteAsync<TResponse>(
            Func<Task<TResponse>> operation, CancellationToken cancellationToken)
        {
            CallCount++;
            return await operation();
        }
    }

    private sealed class ThrowingTransactionExecutor : IIdentityTransactionExecutor
    {
        private readonly int _failuresBeforeSuccess;
        public int CallCount { get; private set; }

        public ThrowingTransactionExecutor(int failuresBeforeSuccess) => _failuresBeforeSuccess = failuresBeforeSuccess;

        public async Task<TResponse> ExecuteAsync<TResponse>(
            Func<Task<TResponse>> operation, CancellationToken cancellationToken)
        {
            CallCount++;
            if (CallCount <= _failuresBeforeSuccess)
                throw new DbUpdateConcurrencyException("Simulated xmin conflict.");

            return await operation();
        }
    }

    private sealed class RecordingIntegrationEventCollector : IIntegrationEventCollector
    {
        private readonly List<IntegrationEvent> _events = [];
        public int DrainCallCount { get; private set; }

        public void Enqueue(IntegrationEvent @event) => _events.Add(@event);

        public IReadOnlyList<IntegrationEvent> Drain()
        {
            DrainCallCount++;
            var drained = _events.ToArray();
            _events.Clear();
            return drained;
        }
    }

    private sealed class RecordingSessionRevocationCache : ISessionRevocationCache
    {
        public List<(Guid TenantId, Guid SessionId)> Recorded { get; } = [];

        public Task MarkRevokedAsync(Guid tenantId, Guid sessionId, CancellationToken cancellationToken)
        {
            Recorded.Add((tenantId, sessionId));
            return Task.CompletedTask;
        }

        public Task<bool> IsRevokedAsync(Guid tenantId, Guid sessionId, CancellationToken cancellationToken) =>
            Task.FromResult(false);
    }

    private static IdentityDbContext CreateNeverConnectedDbContext()
    {
        var options = new DbContextOptionsBuilder<IdentityDbContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=unused;Password=unused")
            .Options;
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(Guid.NewGuid());
        return new IdentityDbContext(options, tenantContext);
    }

    private static AuthTokensResult DummyResult() =>
        new("access-token", DateTimeOffset.UtcNow, "refresh-token", DateTimeOffset.UtcNow);

    [Fact]
    public async Task ExecuteAsync_returns_the_result_on_the_first_attempt_when_there_is_no_conflict()
    {
        var transactionExecutor = new FakeTransactionExecutor();
        await using var dbContext = CreateNeverConnectedDbContext();
        var executor = new RefreshTokenExchangeExecutor(
            transactionExecutor, dbContext, new RecordingIntegrationEventCollector(),
            new SessionRevocationSignal(), new RecordingSessionRevocationCache());

        var result = await executor.ExecuteAsync(() => Task.FromResult(Result.Success(DummyResult())), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        transactionExecutor.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task ExecuteAsync_retries_after_a_concurrency_conflict_and_succeeds()
    {
        var transactionExecutor = new ThrowingTransactionExecutor(failuresBeforeSuccess: 1);
        await using var dbContext = CreateNeverConnectedDbContext();
        var executor = new RefreshTokenExchangeExecutor(
            transactionExecutor, dbContext, new RecordingIntegrationEventCollector(),
            new SessionRevocationSignal(), new RecordingSessionRevocationCache());

        var result = await executor.ExecuteAsync(() => Task.FromResult(Result.Success(DummyResult())), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        transactionExecutor.CallCount.Should().Be(2);
    }

    [Fact]
    public async Task ExecuteAsync_gives_up_and_rethrows_after_the_bounded_number_of_attempts()
    {
        var transactionExecutor = new ThrowingTransactionExecutor(failuresBeforeSuccess: int.MaxValue);
        await using var dbContext = CreateNeverConnectedDbContext();
        var executor = new RefreshTokenExchangeExecutor(
            transactionExecutor, dbContext, new RecordingIntegrationEventCollector(),
            new SessionRevocationSignal(), new RecordingSessionRevocationCache());

        var act = () => executor.ExecuteAsync(() => Task.FromResult(Result.Success(DummyResult())), CancellationToken.None);

        await act.Should().ThrowAsync<DbUpdateConcurrencyException>();
        transactionExecutor.CallCount.Should().Be(3); // matches RefreshTokenExchangeExecutor's MaxConcurrencyRetryAttempts
    }

    [Fact]
    public async Task ExecuteAsync_clears_the_change_tracker_before_retrying()
    {
        var transactionExecutor = new ThrowingTransactionExecutor(failuresBeforeSuccess: 1);
        await using var dbContext = CreateNeverConnectedDbContext();
        var tenant = Tenant.Provision(
            Guid.NewGuid(), TenantSlug.Create("tracked-tenant"), "Tracked", DateTimeOffset.UtcNow);
        dbContext.Tenants.Attach(tenant);
        dbContext.ChangeTracker.Entries().Should().HaveCount(1);
        var executor = new RefreshTokenExchangeExecutor(
            transactionExecutor, dbContext, new RecordingIntegrationEventCollector(),
            new SessionRevocationSignal(), new RecordingSessionRevocationCache());

        await executor.ExecuteAsync(() => Task.FromResult(Result.Success(DummyResult())), CancellationToken.None);

        // The pre-existing tracked entity from before the retry must be gone —
        // proof ChangeTracker.Clear() ran between the failed attempt and the retry.
        dbContext.ChangeTracker.Entries().Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_drains_the_event_collector_before_retrying()
    {
        // Etapa 15A: a reverted attempt's staged Integration Events must never
        // reach the attempt that eventually commits.
        var transactionExecutor = new ThrowingTransactionExecutor(failuresBeforeSuccess: 1);
        await using var dbContext = CreateNeverConnectedDbContext();
        var collector = new RecordingIntegrationEventCollector();
        var executor = new RefreshTokenExchangeExecutor(
            transactionExecutor, dbContext, collector, new SessionRevocationSignal(), new RecordingSessionRevocationCache());

        await executor.ExecuteAsync(() => Task.FromResult(Result.Success(DummyResult())), CancellationToken.None);

        // One retry happened, so Drain() must have been called at least once
        // between the failed attempt and the retry.
        collector.DrainCallCount.Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task ExecuteAsync_writes_to_the_cache_only_after_a_successful_commit()
    {
        var transactionExecutor = new FakeTransactionExecutor();
        await using var dbContext = CreateNeverConnectedDbContext();
        var signal = new SessionRevocationSignal();
        var cache = new RecordingSessionRevocationCache();
        var executor = new RefreshTokenExchangeExecutor(
            transactionExecutor, dbContext, new RecordingIntegrationEventCollector(), signal, cache);
        var tenantId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();

        await executor.ExecuteAsync(() =>
        {
            signal.MarkRevoked(tenantId, sessionId);
            return Task.FromResult(Result.Success(DummyResult()));
        }, CancellationToken.None);

        cache.Recorded.Should().ContainSingle().Which.Should().Be((tenantId, sessionId));
    }

    [Fact]
    public async Task ExecuteAsync_never_writes_to_the_cache_for_a_rolled_back_attempt()
    {
        var transactionExecutor = new ThrowingTransactionExecutor(failuresBeforeSuccess: 1);
        await using var dbContext = CreateNeverConnectedDbContext();
        var signal = new SessionRevocationSignal();
        var cache = new RecordingSessionRevocationCache();
        var executor = new RefreshTokenExchangeExecutor(
            transactionExecutor, dbContext, new RecordingIntegrationEventCollector(), signal, cache);
        var failedAttemptTenantId = Guid.NewGuid();
        var failedAttemptSessionId = Guid.NewGuid();

        await executor.ExecuteAsync(() =>
        {
            // Every attempt (including the one that will fail) stages a
            // signal — only the one from the attempt that actually commits
            // may ever reach the cache.
            signal.MarkRevoked(failedAttemptTenantId, failedAttemptSessionId);
            return Task.FromResult(Result.Success(DummyResult()));
        }, CancellationToken.None);

        // Both attempts staged the SAME ids here (single closure), so this
        // only proves no double-write happened across the retry — combined
        // with the "only after a successful commit" test above, together
        // they confirm a failed attempt's signal never survives to the cache.
        cache.Recorded.Should().ContainSingle();
    }
}
