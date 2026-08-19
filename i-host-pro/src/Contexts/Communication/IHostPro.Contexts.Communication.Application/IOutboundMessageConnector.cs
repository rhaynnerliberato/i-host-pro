namespace IHostPro.Contexts.Communication.Application;

/// <summary>
/// The narrowest port Checkpoint 1 needs — send one rendered message to one
/// destination over one channel (WhatsApp). Deliberately NOT
/// <c>IExternalConnector&lt;T&gt;</c>/a generic provider-adapter/registry
/// (CP1 mandate §12) — there is exactly one real connector concept so far
/// (WhatsApp), and generalizing before a second real one exists would be
/// speculative (mirrors this codebase's own established "3+ contexts before
/// extraction" rule, Architecture Principles §12). CP1's own implementation
/// is a deterministic fake (<c>FakeWhatsAppConnector</c>,
/// <c>Communication.Infrastructure</c>) — no real WhatsApp API is called
/// this checkpoint.
/// </summary>
public interface IOutboundMessageConnector
{
    Task<OutboundMessageDispatchResult> SendAsync(OutboundMessageDispatch dispatch, CancellationToken cancellationToken);
}
