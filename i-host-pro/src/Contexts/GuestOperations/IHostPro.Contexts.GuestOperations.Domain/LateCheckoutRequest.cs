using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.GuestOperations.Domain.Enums;

namespace IHostPro.Contexts.GuestOperations.Domain;

/// <summary>
/// A guest's request to check out later than the Reservation's current
/// <c>CheckOutAt</c> (Fase 10, Checkpoint 3). Evaluation is synchronous and
/// automatic, exactly like <see cref="EarlyCheckInRequest"/>, with one
/// deliberate payment-boundary exception: when the resolved policy's charge
/// requires PIX confirmation, the request settles at
/// <see cref="LateCheckoutRequestStatus.PendingPayment"/> instead of
/// <see cref="LateCheckoutRequestStatus.Approved"/> — this checkpoint stops
/// there; Fase 10, Checkpoint 5 (PIX Payment Boundary) is what eventually
/// moves it onward.
///
/// <see cref="ChargeType"/>/<see cref="ChargeValue"/>/<see cref="RequiresPix"/>
/// are a permanent snapshot of the <c>LateCheckoutPolicy</c> terms this
/// request was evaluated against, captured once at creation — independent of
/// whatever the policy resolves to later (mirrors the same snapshot
/// rationale as <see cref="Enums.LateCheckoutChargeType"/>'s own doc comment).
/// <c>LateCheckoutChargeType.Percentage</c> is officially unsupported
/// (Fase 10, Checkpoint 3 mandate) pending a future pricing domain — the
/// deciding command handler must reject the request with an explicit
/// functional error BEFORE ever calling <see cref="Create"/> when the
/// resolved policy's charge type is <see cref="Enums.LateCheckoutChargeType.Percentage"/>;
/// this aggregate never receives that value.
/// </summary>
public sealed class LateCheckoutRequest : AggregateRoot<Guid>, ITenantOwned
{
    public Guid TenantId { get; private set; }
    public Guid ReservationId { get; private set; }
    public Guid PropertyId { get; private set; }
    public DateTimeOffset RequestedCheckOutAt { get; private set; }
    public LateCheckoutChargeType ChargeType { get; private set; }
    public decimal? ChargeValue { get; private set; }
    public bool RequiresPix { get; private set; }
    public LateCheckoutRequestStatus Status { get; private set; }
    public LateCheckoutDenialReason? DenialReason { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset? DecidedAtUtc { get; private set; }
    public DateTimeOffset UpdatedAtUtc { get; private set; }

    private LateCheckoutRequest()
    {
        // EF Core materialization.
    }

    private LateCheckoutRequest(
        Guid id, Guid tenantId, Guid reservationId, Guid propertyId,
        DateTimeOffset requestedCheckOutAt, LateCheckoutChargeType chargeType,
        decimal? chargeValue, bool requiresPix, DateTimeOffset now)
        : base(id)
    {
        TenantId = tenantId;
        ReservationId = reservationId;
        PropertyId = propertyId;
        RequestedCheckOutAt = requestedCheckOutAt;
        ChargeType = chargeType;
        ChargeValue = chargeValue;
        RequiresPix = requiresPix;
        Status = LateCheckoutRequestStatus.Pending;
        CreatedAtUtc = now;
        UpdatedAtUtc = now;
    }

    public static LateCheckoutRequest Create(
        Guid id, Guid tenantId, Guid reservationId, Guid propertyId,
        DateTimeOffset requestedCheckOutAt, LateCheckoutChargeType chargeType,
        decimal? chargeValue, bool requiresPix, DateTimeOffset now)
    {
        if (reservationId == Guid.Empty)
            throw new ArgumentException("Reservation id cannot be empty.", nameof(reservationId));

        if (propertyId == Guid.Empty)
            throw new ArgumentException("Property id cannot be empty.", nameof(propertyId));

        if (chargeType == LateCheckoutChargeType.Percentage)
        {
            throw new ArgumentException(
                "Percentage charge type is not supported — it must be rejected before a request is created.",
                nameof(chargeType));
        }

        return new LateCheckoutRequest(
            id, tenantId, reservationId, propertyId, requestedCheckOutAt, chargeType, chargeValue, requiresPix, now);
    }

    /// <summary>
    /// <see cref="LateCheckoutRequestStatus.Pending"/> → <see cref="LateCheckoutRequestStatus.Approved"/>
    /// (the non-PIX path), or <see cref="LateCheckoutRequestStatus.PendingPayment"/> →
    /// <see cref="LateCheckoutRequestStatus.Approved"/> (Fase 10, Checkpoint
    /// 5 — PIX/Payment Deterministic Foundation: the exact "transition
    /// onward" <see cref="MarkPendingPayment"/>'s own doc comment anticipated,
    /// called by <c>PixChargeConfirmedLateCheckoutApprover</c> once the
    /// associated PixCharge is confirmed). Terminal either way.
    /// </summary>
    public void Approve(DateTimeOffset now)
    {
        if (Status is not (LateCheckoutRequestStatus.Pending or LateCheckoutRequestStatus.PendingPayment))
            throw new InvalidOperationException($"Cannot approve a late checkout request in status '{Status}'.");

        Status = LateCheckoutRequestStatus.Approved;
        DecidedAtUtc = now;
        UpdatedAtUtc = now;
    }

    /// <summary>
    /// <see cref="LateCheckoutRequestStatus.Pending"/> → <see cref="LateCheckoutRequestStatus.PendingPayment"/>.
    /// NOT terminal — the one state a future checkpoint (Fase 10, Checkpoint 5)
    /// is expected to transition onward from. Reservation's schedule is never
    /// altered while a request sits here.
    /// </summary>
    public void MarkPendingPayment(DateTimeOffset now)
    {
        if (Status != LateCheckoutRequestStatus.Pending)
            throw new InvalidOperationException($"Cannot mark a late checkout request in status '{Status}' as pending payment.");

        Status = LateCheckoutRequestStatus.PendingPayment;
        UpdatedAtUtc = now;
    }

    /// <summary>
    /// <see cref="LateCheckoutRequestStatus.Pending"/> → <see cref="LateCheckoutRequestStatus.Denied"/>.
    /// Terminal. <paramref name="reason"/> must be a known negative business
    /// decision — never used for an infrastructure failure or a missing
    /// precondition, which are surfaced as a failed command result before a
    /// request row is ever created.
    /// </summary>
    public void Deny(LateCheckoutDenialReason reason, DateTimeOffset now)
    {
        if (Status != LateCheckoutRequestStatus.Pending)
            throw new InvalidOperationException($"Cannot deny a late checkout request in status '{Status}'.");

        Status = LateCheckoutRequestStatus.Denied;
        DenialReason = reason;
        DecidedAtUtc = now;
        UpdatedAtUtc = now;
    }
}
