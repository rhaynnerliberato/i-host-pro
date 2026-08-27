namespace IHostPro.Contexts.GuestOperations.Contracts;

/// <summary>
/// Stable ASCII values for <see cref="EarlyCheckinDenied.ReasonCode"/> (Fase
/// 10, Checkpoint 3). Part of the public contract — never rename an existing
/// value, only add new ones. Mirrors <c>EarlyCheckInDenialReason</c>
/// (GuestOperations.Domain) one-for-one.
/// </summary>
public static class EarlyCheckinDeniedReasonCodes
{
    public const string PolicyNotConfigured = "policy_not_configured";
    public const string PolicyNotAllowed = "policy_not_allowed";
    public const string BeforeEarliestTime = "before_earliest_time";
    public const string ScheduleConflict = "schedule_conflict";
    public const string CleaningNotReady = "cleaning_not_ready";
}

/// <summary>
/// Stable ASCII values for <see cref="LateCheckoutDenied.ReasonCode"/> (Fase
/// 10, Checkpoint 3). Part of the public contract — never rename an existing
/// value, only add new ones. Mirrors <c>LateCheckoutDenialReason</c>
/// (GuestOperations.Domain) one-for-one.
/// </summary>
public static class LateCheckoutDeniedReasonCodes
{
    public const string PolicyNotConfigured = "policy_not_configured";
    public const string PolicyNotAllowed = "policy_not_allowed";
    public const string AfterLatestTime = "after_latest_time";
    public const string ScheduleConflict = "schedule_conflict";
}
