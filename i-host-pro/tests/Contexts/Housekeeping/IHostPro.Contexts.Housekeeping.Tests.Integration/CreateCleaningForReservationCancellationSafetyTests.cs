using FluentAssertions;
using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Infrastructure.Messaging;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.BuildingBlocks.Messaging.Abstractions;
using IHostPro.Contexts.Housekeeping.Application.Cleanings;
using IHostPro.Contexts.Housekeeping.Contracts;
using IHostPro.Contexts.Housekeeping.Domain.Enums;
using IHostPro.Contexts.Housekeeping.Infrastructure;
using IHostPro.Contexts.Housekeeping.Infrastructure.Messaging;
using IHostPro.Contexts.Housekeeping.Infrastructure.Persistence;
using IHostPro.Contexts.PropertyManagement.Contracts;
using IHostPro.Contexts.Reservations.Contracts;
using JasperFx;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.Postgresql;

namespace IHostPro.Contexts.Housekeeping.Tests.Integration;

/// <summary>
/// Fase 8, Checkpoint 1.1 — corrective homologation of ADR-018's cancellation
/// race (rejected at CP1 review for being merely best-effort). Proves the
/// real invariant against a real PostgreSQL instance, using the same
/// composition root (<c>AddHousekeepingModule</c>) and real
/// <c>pg_advisory_xact_lock</c>-backed <see cref="IReservationCancellationGuard"/>
/// as production — never a fake lock:
///
/// once every in-flight message for a Reservation has been processed, a
/// cancelled Reservation can NEVER have an ACTIVE automated Cleaning —
/// either none was ever created, or the one that was gets cancelled.
///
/// Reuses <see cref="HousekeepingEventProjectionTests.Fixture"/>'s
/// Postgres container (same connection strings, same provisioned outbox/main
/// store schema) — no separate container needed for these tests.
/// </summary>
public sealed class CreateCleaningForReservationCancellationSafetyTests : IClassFixture<HousekeepingEventProjectionTests.Fixture>
{
    private const string MainSchema = "platform_messaging";

    private readonly HousekeepingEventProjectionTests.Fixture _fixture;

    public CreateCleaningForReservationCancellationSafetyTests(HousekeepingEventProjectionTests.Fixture fixture) =>
        _fixture = fixture;

    // ---- §10: genuine concurrent race, either order, invariant always holds ----

    /// <summary>
    /// Mirrors <c>ReservationCommandHandlerTests.Two_deterministically_synchronized_creates_...</c>'s
    /// own <see cref="Barrier"/> technique: both the command flow and the
    /// cancellation flow are forced to reach
    /// <see cref="IReservationCancellationGuard.AcquireLockAsync"/> at
    /// EXACTLY the same instant, then race for the REAL
    /// <c>pg_advisory_xact_lock</c> — proving mutual exclusion actually
    /// holds under contention, not merely "usually" under incidental
    /// <c>Task.WhenAll</c> scheduling. Two independent hosts/DI containers
    /// (never two clients of the same host). Run several times (fresh
    /// reservation each iteration) so both possible lock-winners are
    /// exercised across the suite — but every single iteration's assertion
    /// is order-agnostic and must hold regardless of which side wins.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task Two_genuinely_concurrent_operations_for_the_same_reservation_never_violate_the_invariant(int _)
    {
        var tenantId = Guid.NewGuid();
        var reservationId = Guid.NewGuid();
        var propertyId = await SeedActivePropertyAsync(tenantId);
        var barrier = new Barrier(2);

        void OverrideGuard(IServiceCollection services) =>
            services.AddScoped<IReservationCancellationGuard>(sp =>
                new BarrierReservationCancellationGuard(
                    new ReservationCancellationGuard(sp.GetRequiredService<HousekeepingDbContext>()), barrier));

        using var host1 = await BuildHostAsync(OverrideGuard);
        using var host2 = await BuildHostAsync(OverrideGuard);

        var commandTask = DispatchCommandAsync(host1, tenantId, reservationId, propertyId);
        var cancelTask = DispatchEventAsync(host2, tenantId, new ReservationCancelled
        {
            TenantId = tenantId, AggregateId = reservationId, AggregateType = "Reservation",
            CorrelationId = Guid.NewGuid(), ActorType = "User", ActorId = Guid.NewGuid().ToString(),
            ReservationId = reservationId, PropertyId = propertyId,
        });

        var whenAll = Task.WhenAll(commandTask, cancelTask);
        var completedInTime = await Task.WhenAny(whenAll, Task.Delay(TimeSpan.FromSeconds(15))) == whenAll;
        completedInTime.Should().BeTrue("both barrier-synchronized operations must complete — a stuck Barrier would indicate a deadlock");

        await AssertNoActiveAutomatedCleaningWhenCancelledAsync(tenantId, reservationId);
        (await ReadReservationProjectionAsync(tenantId, reservationId)).IsCancelled.Should().BeTrue();
    }

    private sealed class BarrierReservationCancellationGuard : IReservationCancellationGuard
    {
        private readonly IReservationCancellationGuard _inner;
        private readonly Barrier _barrier;

        public BarrierReservationCancellationGuard(IReservationCancellationGuard inner, Barrier barrier)
        {
            _inner = inner;
            _barrier = barrier;
        }

        public async Task AcquireLockAsync(Guid tenantId, Guid reservationId, CancellationToken cancellationToken)
        {
            _barrier.SignalAndWait(cancellationToken);
            await _inner.AcquireLockAsync(tenantId, reservationId, cancellationToken);
        }
    }

    // ---- §9: deterministically forced orderings ----

    /// <summary>
    /// Test-only decorator — signals <paramref name="lockAcquired"/> right
    /// after the REAL <c>pg_advisory_xact_lock</c> call returns (i.e., this
    /// caller now genuinely holds it), then blocks until
    /// <paramref name="proceedGate"/> completes before returning control to
    /// the caller — giving the test a deterministic window, with the real
    /// lock held open in Postgres the entire time, to start the OTHER
    /// operation and prove it must wait. Registered ONLY in the test host's
    /// DI container — no test-only hook exists in production code (mirrors
    /// <c>ReservationCommandHandlerTests.GatedSnapshotReservationReader</c>'s
    /// own <see cref="TaskCompletionSource"/> technique).
    /// </summary>
    private sealed class GatedReservationCancellationGuard : IReservationCancellationGuard
    {
        private readonly IReservationCancellationGuard _inner;
        private readonly TaskCompletionSource _lockAcquired;
        private readonly TaskCompletionSource _proceedGate;

        public GatedReservationCancellationGuard(
            IReservationCancellationGuard inner, TaskCompletionSource lockAcquired, TaskCompletionSource proceedGate)
        {
            _inner = inner;
            _lockAcquired = lockAcquired;
            _proceedGate = proceedGate;
        }

        public async Task AcquireLockAsync(Guid tenantId, Guid reservationId, CancellationToken cancellationToken)
        {
            await _inner.AcquireLockAsync(tenantId, reservationId, cancellationToken);
            _lockAcquired.TrySetResult();
            await _proceedGate.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        }
    }

    [Fact]
    public async Task Command_wins_the_lock_first_the_cleaning_it_creates_ends_up_cancelled()
    {
        var tenantId = Guid.NewGuid();
        var reservationId = Guid.NewGuid();
        var propertyId = await SeedActivePropertyAsync(tenantId);

        var lockAcquired = new TaskCompletionSource();
        var proceedGate = new TaskCompletionSource();

        void OverrideGuard(IServiceCollection services) =>
            services.AddScoped<IReservationCancellationGuard>(sp =>
                new GatedReservationCancellationGuard(
                    new ReservationCancellationGuard(sp.GetRequiredService<HousekeepingDbContext>()), lockAcquired, proceedGate));

        using var commandHost = await BuildHostAsync(OverrideGuard);
        using var cancelHost = await BuildHostAsync();

        var commandTask = DispatchCommandAsync(commandHost, tenantId, reservationId, propertyId);
        await lockAcquired.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // The command flow genuinely holds the real transaction-scoped lock
        // right now — starting the cancellation here guarantees its own
        // AcquireLockAsync call happens no earlier than while the lock is
        // still held (it either blocks on the real Postgres lock, or — if
        // it hasn't reached the SQL call yet by the time the gate below is
        // released — simply runs after the command has already committed;
        // either way the final state is identical).
        var cancelTask = DispatchEventAsync(cancelHost, tenantId, new ReservationCancelled
        {
            TenantId = tenantId, AggregateId = reservationId, AggregateType = "Reservation",
            CorrelationId = Guid.NewGuid(), ActorType = "User", ActorId = Guid.NewGuid().ToString(),
            ReservationId = reservationId, PropertyId = propertyId,
        });

        proceedGate.TrySetResult();

        var whenAll = Task.WhenAll(commandTask, cancelTask);
        var completedInTime = await Task.WhenAny(whenAll, Task.Delay(TimeSpan.FromSeconds(15))) == whenAll;
        completedInTime.Should().BeTrue("a stuck gate would indicate a deadlock");

        var cleanings = await ReadAutomatedCleaningsAsync(tenantId, reservationId);
        cleanings.Should().ContainSingle("the command won the lock first and created exactly one automated cleaning");
        cleanings[0].Status.Should().Be(CleaningStatus.Cancelled, "the cancellation, running after, must have cancelled it");
        (await ReadReservationProjectionAsync(tenantId, reservationId)).IsCancelled.Should().BeTrue();
    }

    [Fact]
    public async Task Cancellation_wins_the_lock_first_no_active_automated_cleaning_is_ever_created()
    {
        var tenantId = Guid.NewGuid();
        var reservationId = Guid.NewGuid();
        var propertyId = await SeedActivePropertyAsync(tenantId);

        var lockAcquired = new TaskCompletionSource();
        var proceedGate = new TaskCompletionSource();

        void OverrideGuard(IServiceCollection services) =>
            services.AddScoped<IReservationCancellationGuard>(sp =>
                new GatedReservationCancellationGuard(
                    new ReservationCancellationGuard(sp.GetRequiredService<HousekeepingDbContext>()), lockAcquired, proceedGate));

        using var cancelHost = await BuildHostAsync(OverrideGuard);
        using var commandHost = await BuildHostAsync();

        var cancelTask = DispatchEventAsync(cancelHost, tenantId, new ReservationCancelled
        {
            TenantId = tenantId, AggregateId = reservationId, AggregateType = "Reservation",
            CorrelationId = Guid.NewGuid(), ActorType = "User", ActorId = Guid.NewGuid().ToString(),
            ReservationId = reservationId, PropertyId = propertyId,
        });
        await lockAcquired.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var commandTask = DispatchCommandAsync(commandHost, tenantId, reservationId, propertyId);

        proceedGate.TrySetResult();

        var whenAll = Task.WhenAll(cancelTask, commandTask);
        var completedInTime = await Task.WhenAny(whenAll, Task.Delay(TimeSpan.FromSeconds(15))) == whenAll;
        completedInTime.Should().BeTrue("a stuck gate would indicate a deadlock");

        var cleanings = await ReadAutomatedCleaningsAsync(tenantId, reservationId);
        cleanings.Should().BeEmpty("the cancellation won the lock first and marked the reservation cancelled before " +
            "the command ever got to check — no automated cleaning may ever be created for it");
        (await ReadReservationProjectionAsync(tenantId, reservationId)).IsCancelled.Should().BeTrue();
    }

    // ---- §6: out-of-order event delivery ----

    [Fact]
    public async Task Cancelled_processed_before_Created_leaves_a_cancelled_tombstone_that_survives_the_late_Created()
    {
        var tenantId = Guid.NewGuid();
        var reservationId = Guid.NewGuid();
        using var host = await BuildHostAsync();

        await DispatchEventAsync(host, tenantId, new ReservationCancelled
        {
            TenantId = tenantId, AggregateId = reservationId, AggregateType = "Reservation",
            CorrelationId = Guid.NewGuid(), ActorType = "User", ActorId = Guid.NewGuid().ToString(),
            ReservationId = reservationId, PropertyId = Guid.NewGuid(),
        });

        (await ReadReservationProjectionAsync(tenantId, reservationId)).IsCancelled.Should().BeTrue(
            "ReservationCancelled must be able to materialize its own tombstone even when no ReservationCreated has arrived yet");

        await DispatchEventAsync(host, tenantId, new ReservationCreated
        {
            TenantId = tenantId, AggregateId = reservationId, AggregateType = "Reservation",
            CorrelationId = Guid.NewGuid(), ActorType = "User", ActorId = Guid.NewGuid().ToString(),
            ReservationId = reservationId, PropertyId = Guid.NewGuid(), Status = "confirmed",
            Source = "manual",
        });

        (await ReadReservationProjectionAsync(tenantId, reservationId)).IsCancelled.Should().BeTrue(
            "a late-arriving ReservationCreated must never reset IsCancelled back to false");
    }

    // ---- §7: command arriving before Housekeeping's own ReservationCreated reaction ----

    [Fact]
    public async Task The_command_arriving_before_Housekeepings_own_ReservationCreated_reaction_still_creates_the_reference_and_the_cleaning()
    {
        var tenantId = Guid.NewGuid();
        var reservationId = Guid.NewGuid();
        var propertyId = await SeedActivePropertyAsync(tenantId);
        using var host = await BuildHostAsync();

        (await ReservationProjectionExistsAsync(tenantId, reservationId)).Should().BeFalse(
            "no ReservationCreated has been dispatched yet — the local reference must not exist");

        await DispatchCommandAsync(host, tenantId, reservationId, propertyId);

        (await ReservationProjectionExistsAsync(tenantId, reservationId)).Should().BeTrue(
            "the command must be able to materialize its own reference without a synchronous read back to Reservations");
        var cleanings = await ReadAutomatedCleaningsAsync(tenantId, reservationId);
        cleanings.Should().ContainSingle();
        cleanings[0].Status.Should().Be(CleaningStatus.Pending);
    }

    // ---- §12: command redelivery under the new lock-protected flow ----

    [Fact]
    public async Task A_redelivered_command_never_creates_a_second_automated_cleaning_under_the_lock()
    {
        var tenantId = Guid.NewGuid();
        var reservationId = Guid.NewGuid();
        var propertyId = await SeedActivePropertyAsync(tenantId);
        using var host = await BuildHostAsync();

        await DispatchCommandAsync(host, tenantId, reservationId, propertyId);
        await DispatchCommandAsync(host, tenantId, reservationId, propertyId);

        var cleanings = await ReadAutomatedCleaningsAsync(tenantId, reservationId);
        cleanings.Should().ContainSingle("a redelivered CreateCleaningForReservation must never create a second automated cleaning");
    }

    // ---- Service graph / dispatch helpers ----

    private async Task<IHost> BuildHostAsync(Action<IServiceCollection>? overrideServices = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Housekeeping"] = _fixture.AppConnectionString,
        }).Build();

        var hostBuilder = Host.CreateApplicationBuilder();
        hostBuilder.Services.AddScoped<ITenantContext, TenantContext>();
        hostBuilder.Services.AddHousekeepingModule(configuration);
        overrideServices?.Invoke(hostBuilder.Services);

        hostBuilder.UseWolverine(opts =>
        {
            opts.PersistMessagesWithPostgresql(_fixture.AppConnectionString, MainSchema);
            opts.EnrollAncillaryPostgresqlOutbox(_fixture.AppConnectionString, "housekeeping_messaging", typeof(HousekeepingDbContext));
            opts.AutoBuildMessageStorageOnStartup = AutoCreate.None;
            opts.UseEntityFrameworkCoreTransactions();
        });

        var host = hostBuilder.Build();
        await host.StartAsync();
        return host;
    }

    private static async Task DispatchEventAsync<TEvent>(IHost host, Guid tenantId, TEvent @event)
        where TEvent : IntegrationEvent
    {
        using var scope = host.Services.CreateScope();
        var sp = scope.ServiceProvider;
        sp.GetRequiredService<ITenantContext>().SetTenant(tenantId);

        var handler = sp.GetRequiredKeyedService<IIntegrationEventHandler<TEvent>>(HousekeepingMessageExecutionScope.HandlerKey);
        await handler.HandleAsync(@event, CancellationToken.None);
    }

    private static async Task DispatchCommandAsync(IHost host, Guid tenantId, Guid reservationId, Guid propertyId)
    {
        using var scope = host.Services.CreateScope();
        var sp = scope.ServiceProvider;
        sp.GetRequiredService<ITenantContext>().SetTenant(tenantId);

        var handler = sp.GetRequiredService<ICreateCleaningForReservationHandler>();
        await handler.HandleAsync(new CreateCleaningForReservation
        {
            TenantId = tenantId,
            ReservationId = reservationId,
            PropertyId = propertyId,
            CorrelationId = Guid.NewGuid(),
            CausationId = Guid.NewGuid(),
        }, CancellationToken.None);
    }

    private async Task<Guid> SeedActivePropertyAsync(Guid tenantId)
    {
        using var host = await BuildHostAsync();
        var propertyId = Guid.NewGuid();

        await DispatchEventAsync(host, tenantId, new PropertyCreated
        {
            TenantId = tenantId, AggregateId = propertyId, AggregateType = "Property",
            CorrelationId = Guid.NewGuid(), ActorType = "System", ActorId = null, PropertyId = propertyId, Status = "draft",
        });
        await DispatchEventAsync(host, tenantId, new PropertyActivated
        {
            TenantId = tenantId, AggregateId = propertyId, AggregateType = "Property",
            CorrelationId = Guid.NewGuid(), ActorType = "System", ActorId = null, PropertyId = propertyId,
        });

        return propertyId;
    }

    private async Task AssertNoActiveAutomatedCleaningWhenCancelledAsync(Guid tenantId, Guid reservationId)
    {
        var cleanings = await ReadAutomatedCleaningsAsync(tenantId, reservationId);

        if (cleanings.Count == 0)
            return;

        cleanings.Should().ContainSingle();
        cleanings[0].Status.Should().Be(CleaningStatus.Cancelled,
            "a cancelled reservation may never have an automated cleaning left Pending/Assigned");
    }

    private async Task<List<(Guid Id, CleaningStatus Status)>> ReadAutomatedCleaningsAsync(Guid tenantId, Guid reservationId)
    {
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);

        await using var dbContext = CreateDbContext(_fixture.MigratorConnectionString, tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        var rows = await dbContext.Cleanings
            .Where(c => c.TenantId == tenantId && c.ReservationId == reservationId && c.CreatedByUserId == null)
            .Select(c => new { c.Id, c.Status })
            .ToListAsync();

        return rows.Select(x => (x.Id, x.Status)).ToList();
    }

    private async Task<(bool Exists, bool IsCancelled)> ReadReservationProjectionAsync(Guid tenantId, Guid reservationId)
    {
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);

        await using var dbContext = CreateDbContext(_fixture.MigratorConnectionString, tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        var entry = await dbContext.ReservationProjection
            .FirstOrDefaultAsync(r => r.TenantId == tenantId && r.ReservationId == reservationId);

        return entry is null ? (false, false) : (true, entry.IsCancelled);
    }

    private async Task<bool> ReservationProjectionExistsAsync(Guid tenantId, Guid reservationId) =>
        (await ReadReservationProjectionAsync(tenantId, reservationId)).Exists;

    private static async Task SetTenantAsync(HousekeepingDbContext dbContext, Guid tenantId) =>
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.tenant_id', {tenantId.ToString()}, true)");

    private static HousekeepingDbContext CreateDbContext(string connectionString, ITenantContext tenantContext)
    {
        var options = new DbContextOptionsBuilder<HousekeepingDbContext>()
            .UseNpgsql(connectionString, npgsqlOptions =>
                npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "housekeeping"))
            .Options;

        return new HousekeepingDbContext(options, tenantContext);
    }
}
