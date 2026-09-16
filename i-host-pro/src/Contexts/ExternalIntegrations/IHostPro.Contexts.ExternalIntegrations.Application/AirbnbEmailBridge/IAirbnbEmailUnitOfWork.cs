namespace IHostPro.Contexts.ExternalIntegrations.Application.AirbnbEmailBridge;

/// <summary>
/// Runs one operation inside a tenant-aware, RLS-protected transaction and
/// commits it. Deliberately NOT <c>IExternalIntegrationsTransactionExecutor</c>
/// (that type also drains/publishes this context's Wolverine outbox, which
/// requires the <c>external_integrations_messaging</c> ancillary store to be
/// enrolled in the calling host — the Worker's trimmed
/// <c>AddExternalIntegrationsWhatsAppOutboundProvider</c> registration does
/// not enroll it). This gate never publishes an Integration Event (no
/// reservation mutation), so a plain transaction + <c>SaveChangesAsync</c> is
/// the correct, minimal primitive — reused by <c>AirbnbEmailDeltaSyncRunner</c>
/// for every durability boundary described in its own remarks.
/// </summary>
public interface IAirbnbEmailUnitOfWork
{
    Task<TResult> ExecuteAsync<TResult>(Func<Task<TResult>> operation, CancellationToken cancellationToken);
}
