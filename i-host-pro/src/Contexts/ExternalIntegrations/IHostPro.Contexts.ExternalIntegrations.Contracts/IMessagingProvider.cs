namespace IHostPro.Contexts.ExternalIntegrations.Contracts;

/// <summary>
/// Provider-neutral synchronous port through which Communication executes an
/// outbound message send — the sixth synchronous cross-context exception
/// (ADR-021, Architecture Principles §14). Deliberately provider-neutral: no
/// type, name, or concept specific to a real provider (Meta, Twilio, or any
/// other) may ever appear on this interface or its request/result types —
/// provider-specific DTOs live exclusively in
/// <c>ExternalIntegrations.Infrastructure</c>, never here.
///
/// Not consumed by Communication yet — Checkpoint 2.1 is foundation only
/// (this contract, no Meta HTTP, no real send). The concrete implementation
/// and the DI wiring that lets Communication actually call this belong to
/// Checkpoint 2.2. No artificial wiring was added just to exercise this
/// interface in this checkpoint.
/// </summary>
public interface IMessagingProvider
{
    Task<OutboundMessageResult> SendAsync(OutboundMessageRequest request, CancellationToken cancellationToken);
}
