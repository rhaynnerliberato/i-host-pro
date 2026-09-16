using FluentAssertions;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.ExternalIntegrations.Application.AirbnbEmailBridge;
using IHostPro.Contexts.ExternalIntegrations.Domain;
using Microsoft.Extensions.Logging;

namespace IHostPro.Contexts.ExternalIntegrations.Tests.Unit.Application.AirbnbEmailBridge;

/// <summary>
/// Airbnb Email Bridge Audit Behavior DI Hardening gate: proves
/// <see cref="AuditDisconnectAirbnbEmailMailboxBehavior"/> emits exactly one
/// structured, PII-safe log entry per <see cref="DisconnectAirbnbEmailMailboxCommand"/>
/// attempt — mirrors <c>AuditConnectAirbnbEmailMailboxBehaviorTests</c>.
/// </summary>
public class AuditDisconnectAirbnbEmailMailboxBehaviorTests
{
    private sealed record LoggedEntry(LogLevel Level, Exception? Exception, IReadOnlyList<KeyValuePair<string, object?>> State);

    private sealed class RecordingLogger : ILogger<AuditDisconnectAirbnbEmailMailboxBehavior>
    {
        public List<LoggedEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var values = state as IReadOnlyList<KeyValuePair<string, object?>>
                ?? throw new InvalidOperationException("Expected structured log state (a message template with named placeholders).");
            Entries.Add(new LoggedEntry(logLevel, exception, values));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid ActorUserId = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);

    private static DisconnectAirbnbEmailMailboxCommand Command() => new(TenantId, ActorUserId);

    private static AirbnbEmailMailboxConnectionResult SuccessValue() => new(
        TenantId, MailboxAddress: null, AirbnbEmailAuthorizationStatus.Disconnected, IsEnabled: false, null, Now, Now);

    [Fact]
    public async Task A_successful_disconnect_logs_exactly_one_structured_information_entry_after_next_completes()
    {
        var logger = new RecordingLogger();
        var behavior = new AuditDisconnectAirbnbEmailMailboxBehavior(new FixedTimeProvider(Now), logger);
        var nextCalled = false;

        var result = await behavior.Handle(
            Command(),
            (_, _) =>
            {
                nextCalled = true;
                return ValueTask.FromResult(Result.Success(SuccessValue()));
            },
            CancellationToken.None);

        nextCalled.Should().BeTrue("the behavior must actually invoke the rest of the pipeline (repository read + token cache clear)");
        result.IsSuccess.Should().BeTrue();

        logger.Entries.Should().ContainSingle();
        var entry = logger.Entries[0];
        entry.Level.Should().Be(LogLevel.Information);
        entry.Exception.Should().BeNull();

        var state = entry.State.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        state["TenantId"].Should().Be(TenantId);
        state["ActorUserId"].Should().Be(ActorUserId);
        state["Result"].Should().Be("Success");
        state.Should().ContainKey("Timestamp");
        state.Should().ContainKey("DurationMs");
    }

    [Fact]
    public async Task A_rejected_disconnect_logs_exactly_one_structured_information_entry()
    {
        var logger = new RecordingLogger();
        var behavior = new AuditDisconnectAirbnbEmailMailboxBehavior(new FixedTimeProvider(Now), logger);
        var error = new Error(AirbnbEmailBridgeErrorCodes.ConnectionNotFound, AirbnbEmailBridgeErrorCodes.ConnectionNotFound);

        var result = await behavior.Handle(
            Command(),
            (_, _) => ValueTask.FromResult(Result.Failure<AirbnbEmailMailboxConnectionResult>(error)),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();

        logger.Entries.Should().ContainSingle();
        var state = logger.Entries[0].State.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        state["Result"].Should().Be("Rejected");
    }

    [Fact]
    public async Task A_thrown_exception_logs_exactly_one_structured_error_entry_then_rethrows_without_swallowing()
    {
        var logger = new RecordingLogger();
        var behavior = new AuditDisconnectAirbnbEmailMailboxBehavior(new FixedTimeProvider(Now), logger);
        var failure = new InvalidOperationException("simulated failure");

        var act = async () => await behavior.Handle(Command(), (_, _) => throw failure, CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(failure);

        logger.Entries.Should().ContainSingle();
        var entry = logger.Entries[0];
        entry.Level.Should().Be(LogLevel.Error);
        entry.Exception.Should().BeSameAs(failure);

        var state = entry.State.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        state["Result"].Should().Be("Failed");
        state["ErrorType"].Should().Be(nameof(InvalidOperationException));
    }

    [Fact]
    public async Task The_audit_entry_never_carries_a_key_outside_the_approved_minimal_vocabulary()
    {
        string[] allowedKeys = ["AuditEvent", "TenantId", "ActorUserId", "Timestamp", "Result", "ErrorType", "DurationMs", "{OriginalFormat}"];

        var logger = new RecordingLogger();
        await new AuditDisconnectAirbnbEmailMailboxBehavior(new FixedTimeProvider(Now), logger).Handle(
            Command(), (_, _) => ValueTask.FromResult(Result.Success(SuccessValue())), CancellationToken.None);

        logger.Entries.Should().ContainSingle();
        logger.Entries[0].State.Select(kvp => kvp.Key).Should().BeSubsetOf(allowedKeys);
        logger.Entries[0].State.Select(kvp => kvp.Key).Should().NotContain(["MailboxAddress", "HomeAccountId", "AccountTenantId", "GrantedScopes"]);
    }
}
