namespace IHostPro.Contexts.GuestOperations.Domain.Enums;

/// <summary>
/// The closed, provider-neutral set of reasons an Early Check-in request may
/// be <see cref="EarlyCheckInRequestStatus.Denied"/> for a known BUSINESS
/// reason (Fase 10, Checkpoint 3, mandate §17) — never free text. Reserved
/// exclusively for genuine negative business decisions; a policy engine
/// failure, a missing precondition (Reservation not Confirmed, GuestStay
/// wrong status) or a duplicate active request are handled as
/// <c>Result.Failure</c> BEFORE a request row is ever created, never as a
/// <see cref="Denied"/> outcome with one of these reasons.
/// </summary>
public enum EarlyCheckInDenialReason
{
    PolicyNotConfigured = 0,
    PolicyNotAllowed = 1,
    BeforeEarliestTime = 2,
    ScheduleConflict = 3,
    CleaningNotReady = 4,
}
