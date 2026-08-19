using IHostPro.Contexts.Communication.Application;
using IHostPro.Contexts.ExternalIntegrations.Contracts;

namespace IHostPro.Contexts.Communication.Infrastructure.Messaging;

/// <inheritdoc cref="IOutboundMessageConnector"/>
/// <remarks>
/// Fase 9, Checkpoint 2.2's real <see cref="IOutboundMessageConnector"/>
/// implementation — a thin adapter between Communication's own local
/// dispatch shapes and <see cref="IMessagingProvider"/> (ADR-021's sixth
/// synchronous exception, <c>ExternalIntegrations.Contracts</c>). Deliberately
/// named after the BOUNDED CONTEXT it delegates to, never the real provider
/// behind it (mandate §45: "zero Meta types in Communication") — the actual
/// provider-specific implementation lives exclusively in
/// <c>ExternalIntegrations.Infrastructure.Meta</c>, reached only through the
/// provider-neutral <see cref="IMessagingProvider"/> port.
///
/// NOT registered in <c>AddCommunicationReservationConsumer</c> — Communication's
/// automatic, Wolverine-triggered <c>ReservationCreated</c> flow keeps using
/// <see cref="FakeWhatsAppConnector"/> unchanged this checkpoint
/// (CP2.2 mandate §46-49, Option A: <c>WhatsAppIntegration.IsEnabled</c>
/// stays <see langword="false"/>/unchanged, so wiring this into the automatic
/// flow would silently bypass that gate — not authorized this checkpoint).
/// This class exists and is fully tested so a direct Application-level call
/// (a dedicated test, never the automatic Worker subscription) can prove the
/// real send path end-to-end, including the Message state-transition mapping
/// below.
/// </remarks>
public sealed class ExternalIntegrationsWhatsAppConnector : IOutboundMessageConnector
{
    private readonly IMessagingProvider _messagingProvider;

    public ExternalIntegrationsWhatsAppConnector(IMessagingProvider messagingProvider) => _messagingProvider = messagingProvider;

    public async Task<OutboundMessageDispatchResult> SendAsync(OutboundMessageDispatch dispatch, CancellationToken cancellationToken)
    {
        var request = new OutboundMessageRequest(
            dispatch.TenantId, dispatch.MessageId, Channel: "WhatsApp", dispatch.Destination,
            dispatch.TemplateKey, dispatch.TemplateVariables, dispatch.IdempotencyKey);

        var result = await _messagingProvider.SendAsync(request, cancellationToken);

        // OutboundMessageResult.Accepted/Rejected/DeliveryOutcomeUnknown collapses
        // to Success/FailureReason here — Communication.Domain.Message already
        // stores FailureReason as a free-text code (mirrors the existing
        // "no_contact_available"/"connector_exception" precedent), so the
        // FailureCategory/FailureCode pair maps directly onto it without a new
        // typed field (CP2.2 mandate §27-31: still distinguishable — Rejected
        // carries the provider's own failure code, DeliveryOutcomeUnknown
        // carries that literal code as FailureCategory).
        return result.Accepted
            ? new OutboundMessageDispatchResult(Success: true, result.ProviderMessageId, FailureReason: null)
            : new OutboundMessageDispatchResult(
                Success: false, ProviderMessageId: null,
                FailureReason: result.FailureCategory == ProviderFailureCategory.DeliveryOutcomeUnknown
                    ? "delivery_outcome_unknown"
                    : result.FailureCode ?? "connector_rejected");
    }
}
