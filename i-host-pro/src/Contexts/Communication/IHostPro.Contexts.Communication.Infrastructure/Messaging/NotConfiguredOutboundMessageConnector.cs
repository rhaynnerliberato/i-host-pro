using IHostPro.Contexts.Communication.Application;
using Microsoft.Extensions.Logging;

namespace IHostPro.Contexts.Communication.Infrastructure.Messaging;

/// <inheritdoc cref="IOutboundMessageConnector"/>
/// <remarks>
/// CP5.3E corrective fix — the <see cref="IOutboundMessageConnector"/>
/// registered for every non-Development environment (<see cref="FakeWhatsAppConnector"/>
/// remains Development-only, CP1 mandate §46-49: widening it would silently
/// mark every outbound <c>Message</c> as Sent despite nothing being
/// delivered — exactly the false operational positive that gate exists to
/// prevent). Before this class existed, non-Development environments had no
/// <see cref="IOutboundMessageConnector"/> registration at all, so every
/// Communication outbound-send flow (AI Agent responses, reservation
/// confirmations, front-desk notifications, PIX delivery, guest-access
/// delivery) failed with a raw DI resolution exception instead of an honest,
/// observable business failure. This connector never calls any network/API,
/// never reads a secret, and never reports success — it exists solely to
/// let the handler resolve and fail deterministically and legibly. The real
/// Meta connector (<see cref="ExternalIntegrationsWhatsAppConnector"/>)
/// remains unregistered in Worker until a separate, explicitly-authorized
/// change wires up Worker's Meta/tenant credential access.
/// </remarks>
public sealed class NotConfiguredOutboundMessageConnector : IOutboundMessageConnector
{
    public const string FailureReason = "outbound_channel_not_configured";

    private readonly ILogger<NotConfiguredOutboundMessageConnector> _logger;

    public NotConfiguredOutboundMessageConnector(ILogger<NotConfiguredOutboundMessageConnector> logger) => _logger = logger;

    public Task<OutboundMessageDispatchResult> SendAsync(OutboundMessageDispatch dispatch, CancellationToken cancellationToken)
    {
        // Never logs dispatch.Destination/dispatch.Content — mirrors
        // FakeWhatsAppConnector's own logging discipline; only the
        // idempotency key, which carries no PII.
        _logger.LogWarning(
            "Outbound message connector is not configured for the current runtime environment — idempotencyKey {IdempotencyKey} will not be delivered.",
            dispatch.IdempotencyKey);

        return Task.FromResult(new OutboundMessageDispatchResult(Success: false, ProviderMessageId: null, FailureReason: FailureReason));
    }
}
