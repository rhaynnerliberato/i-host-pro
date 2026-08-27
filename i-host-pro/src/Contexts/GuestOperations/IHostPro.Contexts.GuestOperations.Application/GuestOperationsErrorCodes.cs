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
}
