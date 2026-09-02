using IHostPro.BuildingBlocks.Domain;
using Mediator;
using Microsoft.Extensions.Logging;

namespace IHostPro.Contexts.ExternalIntegrations.Application.WhatsAppTemplateMappings;

/// <summary>
/// Fase 12, Checkpoint 4 (Security/Secrets/LGPD Hardening) — closes the audit
/// gap <see cref="ConfigureWhatsAppTemplateMappingCommand"/> had since Fase 9,
/// Checkpoint 2.2: <see cref="ConfigureWhatsAppTemplateMappingCommand.ActorUserId"/>
/// was already carried by the command but never read anywhere. Clones
/// <c>WhatsAppIntegrations.AuditConfigureWhatsAppIntegrationBehavior</c>'s
/// pattern exactly — same structured-log-only shape, same "Success" only
/// after the inner transaction genuinely commits, same registration ordering
/// (outermost, wrapping <c>TenantTransactionBehavior</c>) — see that class's
/// own doc comment for the full rationale, all of which applies unchanged
/// here. No migration, no new table, no new Architecture Exception: this is
/// the exact same mechanism, applied to the sibling command.
///
/// Fields are deliberately minimal, mirroring the sibling behavior's own
/// restraint: never the template body, never a phone number, never a
/// credential/token/app secret/verify token/provider payload — a template
/// mapping never carries any of those (<see cref="WhatsAppTemplateMappingResult"/>'s
/// own doc comment), so this behavior does not either.
/// </summary>
public sealed class AuditConfigureWhatsAppTemplateMappingBehavior
    : IPipelineBehavior<ConfigureWhatsAppTemplateMappingCommand, Result<WhatsAppTemplateMappingResult>>
{
    private const string AuditEvent = "WhatsAppTemplateMappingConfigurationChanged";
    private const string IntegrationType = "WhatsAppTemplateMapping";
    private const string Action = "Configure";
    private const string ActorType = "User";

    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AuditConfigureWhatsAppTemplateMappingBehavior> _logger;

    public AuditConfigureWhatsAppTemplateMappingBehavior(
        TimeProvider timeProvider, ILogger<AuditConfigureWhatsAppTemplateMappingBehavior> logger)
    {
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async ValueTask<Result<WhatsAppTemplateMappingResult>> Handle(
        ConfigureWhatsAppTemplateMappingCommand message,
        MessageHandlerDelegate<ConfigureWhatsAppTemplateMappingCommand, Result<WhatsAppTemplateMappingResult>> next,
        CancellationToken cancellationToken)
    {
        var startedAtUtc = _timeProvider.GetUtcNow();

        try
        {
            var result = await next(message, cancellationToken);

            var durationMs = (_timeProvider.GetUtcNow() - startedAtUtc).TotalMilliseconds;

            _logger.LogInformation(
                "{AuditEvent}: tenant {TenantId} integration {IntegrationType} action {Action} by {ActorType} " +
                "{ActorUserId} templateKey {TemplateKey} at {Timestamp} — result {Result} in {DurationMs}ms",
                AuditEvent, message.TenantId, IntegrationType, Action, ActorType,
                message.ActorUserId, message.TemplateKey, _timeProvider.GetUtcNow(), "Success", durationMs);

            return result;
        }
        catch (Exception ex)
        {
            var durationMs = (_timeProvider.GetUtcNow() - startedAtUtc).TotalMilliseconds;

            _logger.LogError(
                ex,
                "{AuditEvent}: tenant {TenantId} integration {IntegrationType} action {Action} by {ActorType} " +
                "{ActorUserId} templateKey {TemplateKey} at {Timestamp} — result {Result} ({ErrorType}) in {DurationMs}ms",
                AuditEvent, message.TenantId, IntegrationType, Action, ActorType,
                message.ActorUserId, message.TemplateKey, _timeProvider.GetUtcNow(), "Failed", ex.GetType().Name, durationMs);

            throw;
        }
    }
}
