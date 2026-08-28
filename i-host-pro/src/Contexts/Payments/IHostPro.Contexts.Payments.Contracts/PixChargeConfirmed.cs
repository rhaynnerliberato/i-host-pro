using IHostPro.BuildingBlocks.Messaging.Abstractions;

namespace IHostPro.Contexts.Payments.Contracts;

/// <summary>
/// Published when a <c>PixCharge</c> transitions to <c>Confirmed</c> (Fase
/// 10, Checkpoint 5 — PIX/Payment Deterministic Foundation).
/// <see cref="IntegrationEvent.AggregateId"/>/<see cref="IntegrationEvent.AggregateType"/>
/// are the PixCharge's id/<c>"PixCharge"</c>. <see cref="IntegrationEvent.ActorType"/>
/// is always <c>"System"</c>. Guest Operations is the sole consumer — it
/// looks up the <see cref="LateCheckoutRequestId"/>, verifies the request is
/// still <c>PendingPayment</c>, and calls <c>Approve()</c>, publishing the
/// existing <c>LateCheckoutApproved</c> event (no new logic duplicated —
/// reuses the Checkpoint 3 approval path exactly).
///
/// Never carries provider payload/QR/payer data — mirrors
/// <see cref="PixChargeCreated"/>'s own PII/financial-data absence exactly.
/// </summary>
public sealed record PixChargeConfirmed : IntegrationEvent
{
    public required Guid LateCheckoutRequestId { get; init; }

    public required Guid ReservationId { get; init; }

    public required DateTimeOffset ConfirmedAtUtc { get; init; }
}
