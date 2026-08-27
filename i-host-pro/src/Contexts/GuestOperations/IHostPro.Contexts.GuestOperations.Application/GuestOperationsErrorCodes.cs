namespace IHostPro.Contexts.GuestOperations.Application;

/// <summary>
/// Stable error codes for Guest Operations' write commands (Fase 10,
/// Checkpoint 2 — Check-in/Checkout Core) — mirrors
/// <c>Reservations.Application.Errors.ReservationsErrorCodes</c>'s own
/// convention exactly, translated to HTTP by
/// <c>GuestOperationsResultHttpMapper</c> (Api).
/// </summary>
public static class GuestOperationsErrorCodes
{
    /// <summary>No <c>GuestStayOperation</c> exists for the given (tenant, reservation) — either the reservation is unknown/cross-tenant, or its own creation has not yet been processed (an internal, transient inconsistency, never surfaced as a guest-facing concept).</summary>
    public const string GuestStayOperationNotFound = "guest_stay_operation_not_found";

    /// <summary>Check-in was attempted on an operation already <c>CheckedOut</c> — a terminal-state violation, never restored.</summary>
    public const string GuestStayOperationAlreadyCheckedOut = "guest_stay_operation_already_checked_out";

    /// <summary>Checkout was attempted on an operation still <c>Active</c> — a checkout without a recorded check-in, an operational inconsistency (Fase 10, Checkpoint 2 decision).</summary>
    public const string GuestStayOperationNotCheckedIn = "guest_stay_operation_not_checked_in";

    // Fase 10, Checkpoint 3 — Early Check-in / Late Checkout. All of the
    // codes below reject a request BEFORE any EarlyCheckInRequest/
    // LateCheckoutRequest row is created — a missing precondition, an
    // invalid structural input, a duplicate active request, or the
    // Percentage-charge-type gap are never surfaced as a Denied domain
    // outcome (that is reserved for a genuine negative business decision
    // evaluated against an already-persisted request row).

    /// <summary>No Reservation exists for the given (tenant, reservation) — unknown or cross-tenant id.</summary>
    public const string ReservationNotFound = "reservation_not_found";

    /// <summary>The Reservation exists but is not <c>Confirmed</c> — Early/Late requests only apply to a Confirmed stay.</summary>
    public const string ReservationNotConfirmed = "reservation_not_confirmed";

    /// <summary>An Early Check-in request requires the GuestStayOperation to still be <c>Active</c> (not yet checked in).</summary>
    public const string GuestStayOperationNotEligibleForEarlyCheckIn = "guest_stay_operation_not_eligible_for_early_check_in";

    /// <summary>A Late Checkout request requires the GuestStayOperation to already be <c>CheckedIn</c>.</summary>
    public const string GuestStayOperationNotEligibleForLateCheckout = "guest_stay_operation_not_eligible_for_late_checkout";

    /// <summary>The requested check-in time is not earlier than the Reservation's current <c>CheckInAt</c>.</summary>
    public const string EarlyCheckInRequestInvalidTime = "early_check_in_request_invalid_time";

    /// <summary>The requested checkout time is not later than the Reservation's current <c>CheckOutAt</c>.</summary>
    public const string LateCheckoutRequestInvalidTime = "late_checkout_request_invalid_time";

    /// <summary>A <c>Pending</c> Early Check-in request already exists for this Reservation (cardinality rule — at most one active request per Reservation per type).</summary>
    public const string EarlyCheckInRequestAlreadyActive = "early_check_in_request_already_active";

    /// <summary>A <c>Pending</c> or <c>PendingPayment</c> Late Checkout request already exists for this Reservation (cardinality rule — <c>PendingPayment</c> counts as active).</summary>
    public const string LateCheckoutRequestAlreadyActive = "late_checkout_request_already_active";

    /// <summary>The effective <c>LateCheckoutPolicy.ChargeType</c> is <c>Percentage</c> — officially unsupported pending a future pricing domain (Fase 10, Checkpoint 3 mandate). No request row is created.</summary>
    public const string LateCheckoutChargeTypePercentageUnsupported = "late_checkout_charge_type_percentage_unsupported";
}
