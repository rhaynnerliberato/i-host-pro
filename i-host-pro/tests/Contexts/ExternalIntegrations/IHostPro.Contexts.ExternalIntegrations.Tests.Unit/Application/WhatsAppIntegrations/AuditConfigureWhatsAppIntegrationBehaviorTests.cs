using FluentAssertions;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.ExternalIntegrations.Application.WhatsAppIntegrations;
using Microsoft.Extensions.Logging;

namespace IHostPro.Contexts.ExternalIntegrations.Tests.Unit.Application.WhatsAppIntegrations;

/// <summary>
/// Fase 9, Checkpoint 2.1.1 (corrective audit gate): proves
/// <see cref="AuditConfigureWhatsAppIntegrationBehavior"/> emits exactly one
/// structured, PII-safe log entry per <see cref="ConfigureWhatsAppIntegrationCommand"/>
/// attempt — success or failure, never both, never silent — via a
/// hand-rolled <c>RecordingLogger</c> that captures the structured
/// (key/value) log state, mirroring
/// <c>ReservationCreatedCleaningOrchestratorTests.RecordingLogger</c>'s
/// established pattern exactly. Assertions read the structured state
/// directly rather than parsing the formatted message string.
/// </summary>
public class AuditConfigureWhatsAppIntegrationBehaviorTests
{
    private sealed record LoggedEntry(LogLevel Level, Exception? Exception, IReadOnlyList<KeyValuePair<string, object?>> State);

    private sealed class RecordingLogger : ILogger<AuditConfigureWhatsAppIntegrationBehavior>
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
    private static readonly DateTimeOffset Now = new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);

    private static ConfigureWhatsAppIntegrationCommand Command() => new(
        TenantId, ActorUserId, "SENTINEL_WABA", "SENTINEL_PHONE",
        "SUPER_SECRET_ACCESS_TOKEN", "SUPER_SECRET_APP_SECRET", "SUPER_SECRET_VERIFY_TOKEN");

    private static WhatsAppIntegrationResult SuccessValue() => new(
        TenantId, "SENTINEL_WABA", "SENTINEL_PHONE", IsEnabled: false,
        AccessTokenConfigured: true, AppSecretConfigured: true, VerifyTokenConfigured: true, Now, Now);

    [Fact]
    public async Task A_successful_configuration_logs_exactly_one_structured_information_entry_after_next_completes()
    {
        var logger = new RecordingLogger();
        var behavior = new AuditConfigureWhatsAppIntegrationBehavior(new FixedTimeProvider(Now), logger);
        var nextCalled = false;

        var result = await behavior.Handle(
            Command(),
            (_, _) =>
            {
                nextCalled = true;
                return ValueTask.FromResult(Result.Success(SuccessValue()));
            },
            CancellationToken.None);

        nextCalled.Should().BeTrue("the behavior must actually invoke the rest of the pipeline (validation + transaction + handler + commit)");
        result.IsSuccess.Should().BeTrue();

        logger.Entries.Should().ContainSingle();
        var entry = logger.Entries[0];
        entry.Level.Should().Be(LogLevel.Information);
        entry.Exception.Should().BeNull();

        var state = entry.State.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        state["AuditEvent"].Should().Be("WhatsAppIntegrationConfigurationChanged");
        state["TenantId"].Should().Be(TenantId);
        state["IntegrationType"].Should().Be("WhatsApp");
        state["Action"].Should().Be("Configure");
        state["ActorType"].Should().Be("User");
        state["ActorUserId"].Should().Be(ActorUserId);
        state["Result"].Should().Be("Success");
        state.Should().ContainKey("Timestamp");
        state.Should().ContainKey("DurationMs");
    }

    [Fact]
    public async Task A_failed_configuration_logs_exactly_one_structured_error_entry_then_rethrows_without_swallowing()
    {
        var logger = new RecordingLogger();
        var behavior = new AuditConfigureWhatsAppIntegrationBehavior(new FixedTimeProvider(Now), logger);
        var failure = new InvalidOperationException("simulated persistence failure");

        var act = async () => await behavior.Handle(
            Command(),
            (_, _) => throw failure,
            CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(failure);

        logger.Entries.Should().ContainSingle();
        var entry = logger.Entries[0];
        entry.Level.Should().Be(LogLevel.Error);
        entry.Exception.Should().BeSameAs(failure);

        var state = entry.State.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        state["AuditEvent"].Should().Be("WhatsAppIntegrationConfigurationChanged");
        state["TenantId"].Should().Be(TenantId);
        state["ActorUserId"].Should().Be(ActorUserId);
        state["Result"].Should().Be("Failed");
        state["ErrorType"].Should().Be(nameof(InvalidOperationException));
    }

    [Fact]
    public async Task Success_never_logs_before_next_returns()
    {
        var logger = new RecordingLogger();
        var behavior = new AuditConfigureWhatsAppIntegrationBehavior(new FixedTimeProvider(Now), logger);
        var loggedBeforeNextCompleted = false;

        await behavior.Handle(
            Command(),
            async (_, _) =>
            {
                // At the point next() runs (which represents the entire
                // inner pipeline — validation, transaction, handler,
                // commit), nothing has been logged yet.
                loggedBeforeNextCompleted = logger.Entries.Count > 0;
                await Task.Yield();
                return Result.Success(SuccessValue());
            },
            CancellationToken.None);

        loggedBeforeNextCompleted.Should().BeFalse(
            "Result=Success must only be logged after the inner pipeline (including the real commit) has completed");
        logger.Entries.Should().ContainSingle();
    }

    [Fact]
    public async Task No_secret_or_non_essential_field_ever_appears_in_the_success_or_failure_audit_entry()
    {
        // Sentinel values chosen so any leak (even partial, e.g. inside a
        // formatted string) is trivially detectable — CP2.1.1 mandate §19.
        string[] forbiddenSubstrings =
        [
            "SUPER_SECRET_ACCESS_TOKEN", "SUPER_SECRET_APP_SECRET", "SUPER_SECRET_VERIFY_TOKEN",
            "SENTINEL_WABA", "SENTINEL_PHONE",
        ];

        var successLogger = new RecordingLogger();
        await new AuditConfigureWhatsAppIntegrationBehavior(new FixedTimeProvider(Now), successLogger).Handle(
            Command(), (_, _) => ValueTask.FromResult(Result.Success(SuccessValue())), CancellationToken.None);

        var failureLogger = new RecordingLogger();
        var act = async () => await new AuditConfigureWhatsAppIntegrationBehavior(new FixedTimeProvider(Now), failureLogger).Handle(
            Command(), (_, _) => throw new InvalidOperationException("boom"), CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>();

        foreach (var logger in new[] { successLogger, failureLogger })
        {
            logger.Entries.Should().ContainSingle();
            var allValues = logger.Entries[0].State
                .Select(kvp => kvp.Value?.ToString() ?? string.Empty)
                .Concat([logger.Entries[0].Exception?.ToString() ?? string.Empty]);

            foreach (var value in allValues)
            {
                foreach (var forbidden in forbiddenSubstrings)
                    value.Should().NotContain(forbidden);
            }
        }
    }

    [Fact]
    public async Task The_audit_entry_never_carries_a_key_outside_the_approved_minimal_vocabulary()
    {
        string[] allowedKeys =
        [
            "AuditEvent", "TenantId", "IntegrationType", "Action", "ActorType", "ActorUserId",
            "Timestamp", "Result", "DurationMs", "ErrorType", "{OriginalFormat}",
        ];

        var successLogger = new RecordingLogger();
        await new AuditConfigureWhatsAppIntegrationBehavior(new FixedTimeProvider(Now), successLogger).Handle(
            Command(), (_, _) => ValueTask.FromResult(Result.Success(SuccessValue())), CancellationToken.None);

        var failureLogger = new RecordingLogger();
        var act = async () => await new AuditConfigureWhatsAppIntegrationBehavior(new FixedTimeProvider(Now), failureLogger).Handle(
            Command(), (_, _) => throw new InvalidOperationException("boom"), CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>();

        successLogger.Entries.Should().ContainSingle();
        failureLogger.Entries.Should().ContainSingle();
        successLogger.Entries[0].State.Select(kvp => kvp.Key).Should().BeSubsetOf(allowedKeys);
        failureLogger.Entries[0].State.Select(kvp => kvp.Key).Should().BeSubsetOf(allowedKeys);

        // WabaId/PhoneNumberId are not secrets, but the mandate explicitly
        // says they are unnecessary to prove the mutation happened — keep
        // the vocabulary minimal.
        successLogger.Entries[0].State.Select(kvp => kvp.Key).Should().NotContain(["WabaId", "PhoneNumberId"]);
    }
}
