using IHostPro.BuildingBlocks.Messaging.Abstractions;

namespace IHostPro.Contexts.GuestOperations.Contracts;

/// <summary>
/// Published when a <c>LateCheckoutRequest</c> settles at
/// <c>PendingPayment</c> (Fase 10, Checkpoint 5 — PIX/Payment Deterministic
/// Foundation; approved decision: async boundary, no synchronous
/// GuestOperations → Payments call). <see cref="IntegrationEvent.AggregateId"/>/
/// <see cref="IntegrationEvent.AggregateType"/> are the LateCheckoutRequest's
/// id/<c>"LateCheckoutRequest"</c>. <see cref="IntegrationEvent.ActorType"/>
/// is always <c>"System"</c>. Payments is the sole consumer — it creates a
/// new <c>PixCharge</c> and calls the (fake, this checkpoint) PIX provider.
///
/// Deliberately provider-neutral and minimal — no GuestName, no GuestPhone,
/// no CPF, no QR/copy-paste payload, no provider id, no provider payload
/// (mandate item 4). <see cref="Amount"/> is a snapshot of
/// <c>LateCheckoutRequest.ChargeValue</c> as already resolved at Checkpoint
/// 3 — Payments never re-reads or recalculates the policy.
/// <see cref="CurrencyCode"/> is always <c>"BRL"</c> this checkpoint
/// (mandate item 6).
/// </summary>
public sealed record LateCheckoutPaymentRequired : IntegrationEvent
{
    public required Guid LateCheckoutRequestId { get; init; }

    public required Guid ReservationId { get; init; }

    public required decimal Amount { get; init; }

    public required string CurrencyCode { get; init; }

    public required DateTimeOffset OccurredAtUtc { get; init; }
}
