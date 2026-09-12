using IHostPro.BuildingBlocks.Domain;
using Mediator;
using Microsoft.Extensions.Logging;

namespace IHostPro.Contexts.ExternalIntegrations.Application.WhatsAppIntegrations;

/// <summary>
/// Emits ONE structured, PII-safe log entry per <see cref="DisableWhatsAppIntegrationCommand"/>
/// attempt — mirrors <see cref="AuditEnableWhatsAppIntegrationBehavior"/> exactly.
/// </summary>
public sealed class AuditDisableWhatsAppIntegrationBehavior
    : IPipelineBehavior<DisableWhatsAppIntegrationCommand, Result<WhatsAppIntegrationResult>>
{
    private const string AuditEvent = "WhatsAppIntegrationConfigurationChanged";
    private const string IntegrationType = "WhatsApp";
    private const string Action = "Disable";
    private const string ActorType = "User";

    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AuditDisableWhatsAppIntegrationBehavior> _logger;

    public AuditDisableWhatsAppIntegrationBehavior(TimeProvider timeProvider, ILogger<AuditDisableWhatsAppIntegrationBehavior> logger)
    {
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async ValueTask<Result<WhatsAppIntegrationResult>> Handle(
        DisableWhatsAppIntegrationCommand message,
        MessageHandlerDelegate<DisableWhatsAppIntegrationCommand, Result<WhatsAppIntegrationResult>> next,
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
