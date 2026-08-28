using IHostPro.BuildingBlocks.Messaging.Abstractions;

namespace IHostPro.Contexts.Payments.Contracts;

/// <summary>
/// Published when a <c>PixCharge</c> is accepted by the (fake, this
/// checkpoint) PIX provider (Fase 10, Checkpoint 5 — PIX/Payment
/// Deterministic Foundation). <see cref="IntegrationEvent.AggregateId"/>/
/// <see cref="IntegrationEvent.AggregateType"/> are the PixCharge's id/
/// <c>"PixCharge"</c>. <see cref="IntegrationEvent.ActorType"/> is always
/// <c>"System"</c>.
///
/// Deliberately provider-neutral and free of any financial payload: never
/// carries <c>QrCodePayload</c>, <c>ProviderChargeId</c>, or any other
/// provider-specific/sensitive data — Communication resolves those
/// separately, synchronously, through <see cref="IPixChargeDeliveryReader"/>
/// (ADR-027, Exception #11) at the moment it actually needs to deliver the
/// charge to the guest. Never carries <c>GuestPhone</c>/<c>GuestName</c>/any
/// payer PII — Communication resolves guest contact through the existing
/// <c>IReservationGuestContactReader</c> (ADR-019).
/// </summary>
public sealed record PixChargeCreated : IntegrationEvent
{
    public required Guid LateCheckoutRequestId { get; init; }

    public required Guid ReservationId { get; init; }
}
