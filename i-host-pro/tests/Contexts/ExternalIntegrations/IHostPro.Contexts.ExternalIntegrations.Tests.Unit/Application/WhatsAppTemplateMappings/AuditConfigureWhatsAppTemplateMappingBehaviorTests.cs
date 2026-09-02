using FluentAssertions;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.ExternalIntegrations.Application.WhatsAppTemplateMappings;
using Microsoft.Extensions.Logging;

namespace IHostPro.Contexts.ExternalIntegrations.Tests.Unit.Application.WhatsAppTemplateMappings;

/// <summary>
/// Fase 12, Checkpoint 4 (Security/Secrets/LGPD Hardening) — mirrors
/// <c>WhatsAppIntegrations.AuditConfigureWhatsAppIntegrationBehaviorTests</c>
/// exactly (same <c>RecordingLogger</c> pattern), for the sibling behavior
/// that closes the gap where <see cref="ConfigureWhatsAppTemplateMappingCommand.ActorUserId"/>
/// was carried but never read.
/// </summary>
public class AuditConfigureWhatsAppTemplateMappingBehaviorTests
{
    private sealed record LoggedEntry(LogLevel Level, Exception? Exception, IReadOnlyList<KeyValuePair<string, object?>> State);

    private sealed class RecordingLogger : ILogger<AuditConfigureWhatsAppTemplateMappingBehavior>
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
    private static readonly DateTimeOffset Now = new(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);

    private static ConfigureWhatsAppTemplateMappingCommand Command() => new(
        TenantId, ActorUserId, "SENTINEL_TEMPLATE_KEY", "SENTINEL_PROVIDER_TEMPLATE_NAME",
        "pt_BR", ["guestName", "checkInDate"]);

    private static WhatsAppTemplateMappingResult SuccessValue() => new(
        TenantId, "SENTINEL_TEMPLATE_KEY", "SENTINEL_PROVIDER_TEMPLATE_NAME", "pt_BR",
        ["guestName", "checkInDate"], Now, Now);

    [Fact]
    public async Task A_successful_configuration_logs_exactly_one_structured_information_entry_after_next_completes()
    {
        var logger = new RecordingLogger();
        var behavior = new AuditConfigureWhatsAppTemplateMappingBehavior(new FixedTimeProvider(Now), logger);
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
        state["AuditEvent"].Should().Be("WhatsAppTemplateMappingConfigurationChanged");
        state["TenantId"].Should().Be(TenantId);
        state["IntegrationType"].Should().Be("WhatsAppTemplateMapping");
        state["Action"].Should().Be("Configure");
        state["ActorType"].Should().Be("User");
        state["ActorUserId"].Should().Be(ActorUserId);
        state["TemplateKey"].Should().Be("SENTINEL_TEMPLATE_KEY");
        state["Result"].Should().Be("Success");
        state.Should().ContainKey("Timestamp");
        state.Should().ContainKey("DurationMs");
    }

    [Fact]
    public async Task A_failed_configuration_logs_exactly_one_structured_error_entry_then_rethrows_without_swallowing()
    {
        var logger = new RecordingLogger();
        var behavior = new AuditConfigureWhatsAppTemplateMappingBehavior(new FixedTimeProvider(Now), logger);
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
        state["AuditEvent"].Should().Be("WhatsAppTemplateMappingConfigurationChanged");
        state["TenantId"].Should().Be(TenantId);
        state["ActorUserId"].Should().Be(ActorUserId);
        state["Result"].Should().Be("Failed");
        state["ErrorType"].Should().Be(nameof(InvalidOperationException));
    }

    [Fact]
    public async Task Success_never_logs_before_next_returns()
    {
        var logger = new RecordingLogger();
        var behavior = new AuditConfigureWhatsAppTemplateMappingBehavior(new FixedTimeProvider(Now), logger);
        var loggedBeforeNextCompleted = false;

        await behavior.Handle(
            Command(),
            async (_, _) =>
            {
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
    public async Task The_audit_entry_never_carries_a_key_outside_the_approved_minimal_vocabulary()
    {
        string[] allowedKeys =
        [
            "AuditEvent", "TenantId", "IntegrationType", "Action", "ActorType", "ActorUserId",
            "TemplateKey", "Timestamp", "Result", "DurationMs", "ErrorType", "{OriginalFormat}",
        ];

        var successLogger = new RecordingLogger();
        await new AuditConfigureWhatsAppTemplateMappingBehavior(new FixedTimeProvider(Now), successLogger).Handle(
            Command(), (_, _) => ValueTask.FromResult(Result.Success(SuccessValue())), CancellationToken.None);

        var failureLogger = new RecordingLogger();
        var act = async () => await new AuditConfigureWhatsAppTemplateMappingBehavior(new FixedTimeProvider(Now), failureLogger).Handle(
            Command(), (_, _) => throw new InvalidOperationException("boom"), CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>();

        successLogger.Entries.Should().ContainSingle();
        failureLogger.Entries.Should().ContainSingle();
        successLogger.Entries[0].State.Select(kvp => kvp.Key).Should().BeSubsetOf(allowedKeys);
        failureLogger.Entries[0].State.Select(kvp => kvp.Key).Should().BeSubsetOf(allowedKeys);

        // Never the template body, phone number, credential, token, app
        // secret, verify token, or provider payload — a template mapping
        // never carries any of those (mandate CP4 §12).
        successLogger.Entries[0].State.Select(kvp => kvp.Key).Should().NotContain(
            ["TemplateBody", "PhoneNumber", "Credential", "Token", "AppSecret", "VerifyToken", "ProviderPayload"]);
    }
}
