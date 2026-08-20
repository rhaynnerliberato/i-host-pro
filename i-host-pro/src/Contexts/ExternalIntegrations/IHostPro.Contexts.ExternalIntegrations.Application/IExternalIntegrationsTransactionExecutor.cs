namespace IHostPro.Contexts.ExternalIntegrations.Application;

/// <summary>
/// Runs an operation inside a tenant-aware, RLS-protected transaction,
/// publishing any collected Integration Event to this context's durable
/// outbox (<c>external_integrations_messaging</c> schema) atomically —
/// mirrors <c>Reservations.Application.IReservationsTransactionExecutor</c>
/// exactly (Fase 9, Checkpoint 2.3.3, ADR-022 item 13).
///
/// Deliberately separate from the generic <c>TenantTransactionBehavior</c>
/// already used by <c>ConfigureWhatsAppIntegrationCommandHandler</c> — that
/// pipeline has no outbox involvement and must stay that way (it never
/// publishes anything); this executor is used exclusively by the webhook
/// status-event publishing path, called directly from Application/Infrastructure,
/// never through the Mediator command pipeline.
/// </summary>
public interface IExternalIntegrationsTransactionExecutor
{
    Task<TResponse> ExecuteAsync<TResponse>(
        Func<Task<TResponse>> operation, CancellationToken cancellationToken);
}
