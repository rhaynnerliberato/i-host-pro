using IHostPro.BuildingBlocks.Domain;
using Mediator;
using Microsoft.Extensions.Logging;

namespace IHostPro.Contexts.ExternalIntegrations.Application.WhatsAppIntegrations;

/// <summary>
/// Emits ONE structured, PII-safe log entry per <see cref="EnableWhatsAppIntegrationCommand"/>
/// attempt — clones <see cref="AuditConfigureWhatsAppIntegrationBehavior"/>'s
/// own pattern exactly, including registration order (outermost, wrapping
/// <c>TenantTransactionBehavior</c>) so "Success" logs only after the inner
/// transaction genuinely commits.
/// </summary>
public sealed class AuditEnableWhatsAppIntegrationBehavior
    : IPipelineBehavior<EnableWhatsAppIntegrationCommand, Result<WhatsAppIntegrationResult>>
{
    private const string AuditEvent = "WhatsAppIntegrationConfigurationChanged";
    private const string IntegrationType = "WhatsApp";
    private const string Action = "Enable";
    private const string ActorType = "User";

    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AuditEnableWhatsAppIntegrationBehavior> _logger;

    public AuditEnableWhatsAppIntegrationBehavior(TimeProvider timeProvider, ILogger<AuditEnableWhatsAppIntegrationBehavior> logger)
    {
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async ValueTask<Result<WhatsAppIntegrationResult>> Handle(
        EnableWhatsAppIntegrationCommand message,
        MessageHandlerDelegate<EnableWhatsAppIntegrationCommand, Result<WhatsAppIntegrationResult>> next,
        CancellationToken cancellationToken)
    {
        var startedAtUtc = _timeProvider.GetUtcNow();

        try
        {
            var result = await next(message, cancellationToken);

            var durationMs = (_timeProvider.GetUtcNow() - startedAtUtc).TotalMilliseconds;

            _logger.LogInformation(
                "{AuditEvent}: tenant {TenantId} integration {IntegrationType} action {Action} by {ActorType} " +
                "{ActorUserId} at {Timestamp} — result {Result} in {DurationMs}ms",
                AuditEvent, message.TenantId, IntegrationType, Action, ActorType,
                message.ActorUserId, _timeProvider.GetUtcNow(), result.IsSuccess ? "Success" : "Rejected", durationMs);

            return result;
        }
        catch (Exception ex)
        {
            var durationMs = (_timeProvider.GetUtcNow() - startedAtUtc).TotalMilliseconds;

            _logger.LogError(
                ex,
                "{AuditEvent}: tenant {TenantId} integration {IntegrationType} action {Action} by {ActorType} " +
                "{ActorUserId} at {Timestamp} — result {Result} ({ErrorType}) in {DurationMs}ms",
                AuditEvent, message.TenantId, IntegrationType, Action, ActorType,
                message.ActorUserId, _timeProvider.GetUtcNow(), "Failed", ex.GetType().Name, durationMs);

            throw;
        }
    }
}
