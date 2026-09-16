using FluentAssertions;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.ExternalIntegrations.Application.AirbnbEmailBridge;
using IHostPro.Contexts.ExternalIntegrations.Domain;
using Microsoft.Extensions.Logging;

namespace IHostPro.Contexts.ExternalIntegrations.Tests.Unit.Application.AirbnbEmailBridge;

/// <summary>
/// Airbnb Email Bridge Audit Behavior DI Hardening gate: proves
/// <see cref="AuditConnectAirbnbEmailMailboxBehavior"/> emits exactly one
/// structured, PII-safe log entry per <see cref="ConnectAirbnbEmailMailboxCommand"/>
/// attempt — mirrors <c>AuditConfigureWhatsAppIntegrationBehaviorTests</c>'s
/// own RecordingLogger pattern exactly.
/// </summary>
public class AuditConnectAirbnbEmailMailboxBehaviorTests
{
    private sealed record LoggedEntry(LogLevel Level, Exception? Exception, IReadOnlyList<KeyValuePair<string, object?>> State);

    private sealed class RecordingLogger : ILogger<AuditConnectAirbnbEmailMailboxBehavior>
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

    private static ConnectAirbnbEmailMailboxCommand Command() => new(TenantId, ActorUserId);

    private static AirbnbEmailMailboxConnectionResult SuccessValue() => new(
        TenantId, "SENTINEL_MAILBOX_ADDRESS@example.com", AirbnbEmailAuthorizationStatus.Connected, IsEnabled: true, Now, Now, Now);

    [Fact]
    public async Task A_successful_connect_logs_exactly_one_structured_information_entry_after_next_completes()
    {
        var logger = new RecordingLogger();
        var behavior = new AuditConnectAirbnbEmailMailboxBehavior(new FixedTimeProvider(Now), logger);
        var nextCalled = false;

        var result = await behavior.Handle(
            Command(),
            (_, _) =>
            {
                nextCalled = true;
                return ValueTask.FromResult(Result.Success(SuccessValue()));
            },
            CancellationToken.None);

        nextCalled.Should().BeTrue("the behavior must actually invoke the rest of the pipeline (authenticator + handler)");
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
    public async Task A_rejected_connect_logs_exactly_one_structured_information_entry_with_the_error_code()
    {
        var logger = new RecordingLogger();
        var behavior = new AuditConnectAirbnbEmailMailboxBehavior(new FixedTimeProvider(Now), logger);
        var error = new Error(AirbnbEmailBridgeErrorCodes.UserCancelled, AirbnbEmailBridgeErrorCodes.UserCancelled);

        var result = await behavior.Handle(
            Command(),
            (_, _) => ValueTask.FromResult(Result.Failure<AirbnbEmailMailboxConnectionResult>(error)),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();

        logger.Entries.Should().ContainSingle();
        var state = logger.Entries[0].State.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        state["Result"].Should().Be("Rejected");
        state["ErrorCode"].Should().Be(AirbnbEmailBridgeErrorCodes.UserCancelled);
    }

    [Fact]
    public async Task A_thrown_exception_logs_exactly_one_structured_error_entry_then_rethrows_without_swallowing()
    {
        var logger = new RecordingLogger();
        var behavior = new AuditConnectAirbnbEmailMailboxBehavior(new FixedTimeProvider(Now), logger);
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
    public async Task No_mailbox_or_identity_PII_ever_appears_in_the_audit_entry()
    {
        string[] forbiddenSubstrings = ["SENTINEL_MAILBOX_ADDRESS", "home-account", "entra-tenant", "Mail.Read"];

        var logger = new RecordingLogger();
        await new AuditConnectAirbnbEmailMailboxBehavior(new FixedTimeProvider(Now), logger).Handle(
            Command(), (_, _) => ValueTask.FromResult(Result.Success(SuccessValue())), CancellationToken.None);

        logger.Entries.Should().ContainSingle();
        var allValues = logger.Entries[0].State.Select(kvp => kvp.Value?.ToString() ?? string.Empty);

        foreach (var value in allValues)
        {
            foreach (var forbidden in forbiddenSubstrings)
                value.Should().NotContain(forbidden);
        }
    }

    [Fact]
    public async Task The_audit_entry_never_carries_a_key_outside_the_approved_minimal_vocabulary()
    {
        string[] allowedKeys = ["AuditEvent", "TenantId", "ActorUserId", "Timestamp", "Result", "ErrorCode", "ErrorType", "DurationMs", "{OriginalFormat}"];

        var logger = new RecordingLogger();
        await new AuditConnectAirbnbEmailMailboxBehavior(new FixedTimeProvider(Now), logger).Handle(
            Command(), (_, _) => ValueTask.FromResult(Result.Success(SuccessValue())), CancellationToken.None);

        logger.Entries.Should().ContainSingle();
        logger.Entries[0].State.Select(kvp => kvp.Key).Should().BeSubsetOf(allowedKeys);
        logger.Entries[0].State.Select(kvp => kvp.Key).Should().NotContain(["MailboxAddress", "HomeAccountId", "AccountTenantId", "GrantedScopes"]);
    }
}
