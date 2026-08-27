namespace IHostPro.Contexts.GuestOperations.Domain.Enums;

/// <summary>
/// The closed, provider-neutral set of reasons a Late Checkout request may
/// be <see cref="LateCheckoutRequestStatus.Denied"/> for a known BUSINESS
/// reason (Fase 10, Checkpoint 3, mandate §31) — mirrors
/// <see cref="EarlyCheckInDenialReason"/>'s own rationale exactly.
/// </summary>
public enum LateCheckoutDenialReason
{
    PolicyNotConfigured = 0,
    PolicyNotAllowed = 1,
    AfterLatestTime = 2,
    ScheduleConflict = 3,
}
