using IHostPro.BuildingBlocks.Domain;
using Mediator;
using Microsoft.Extensions.Logging;

namespace IHostPro.Contexts.ExternalIntegrations.Application.WhatsAppIntegrations;

/// <summary>
/// Fase 9, Checkpoint 2.1.1 (corrective audit gate — CP2.1's report declared
/// "nenhuma auditoria implementada", which the mandate had actually required
/// proportional to Documento 17 §28; this closes that gap). Emits ONE
/// structured, PII-safe log entry per administrative
/// <see cref="ConfigureWhatsAppIntegrationCommand"/> attempt — success or
/// failure, never both, never silent — mirroring
/// <c>ReservationCreatedCleaningOrchestrator</c>'s own established pattern
/// (Workflow.Application, ADR-018 §Checkpoint 2.1) and
/// <c>Identity.Application</c>'s <c>ILogger&lt;T&gt;</c> convention exactly.
///
/// Deliberately a <see cref="IPipelineBehavior{TMessage,TResponse}"/> —
/// registered to wrap AROUND <c>TenantTransactionBehavior</c> (see
/// <c>ExternalIntegrationsCommandDispatchExtensions</c>'s registration
/// order, which this codebase's own <c>ConfigurationCommandDispatchExtensions</c>
/// comment already documents as execution order: first-registered runs
/// outermost) — never logic added inside the command handler itself. This
/// is the only way to log <c>Result = "Success"</c> only after the
/// transaction genuinely committed (CP2.1.1 mandate §11) without changing
/// the transaction boundary itself (mandate §16 explicitly forbids that) —
/// the commit happens inside <c>TenantAwareUnitOfWork.ExecuteAsync</c>,
/// which completes entirely within the inner <c>next()</c> call this
/// behavior awaits, before this behavior ever logs anything.
///
/// Fields are deliberately minimal (CP2.1.1 mandate §14/§15): never
/// <c>WabaId</c>/<c>PhoneNumberId</c> (not secrets, but unnecessary to prove
/// the mutation happened) and never any secret/secret-reference value.
/// <c>ErrorType</c> on failure is the exception's CLR type name only — never
/// the exception message (which could echo back a submitted configuration
/// value) and never a stack trace.
/// </summary>
public sealed class AuditConfigureWhatsAppIntegrationBehavior
    : IPipelineBehavior<ConfigureWhatsAppIntegrationCommand, Result<WhatsAppIntegrationResult>>
{
    private const string AuditEvent = "WhatsAppIntegrationConfigurationChanged";
    private const string IntegrationType = "WhatsApp";
    private const string Action = "Configure";
    private const string ActorType = "User";

    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AuditConfigureWhatsAppIntegrationBehavior> _logger;

    public AuditConfigureWhatsAppIntegrationBehavior(
        TimeProvider timeProvider, ILogger<AuditConfigureWhatsAppIntegrationBehavior> logger)
    {
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async ValueTask<Result<WhatsAppIntegrationResult>> Handle(
        ConfigureWhatsAppIntegrationCommand message,
        MessageHandlerDelegate<ConfigureWhatsAppIntegrationCommand, Result<WhatsAppIntegrationResult>> next,
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
                message.ActorUserId, _timeProvider.GetUtcNow(), "Success", durationMs);

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
