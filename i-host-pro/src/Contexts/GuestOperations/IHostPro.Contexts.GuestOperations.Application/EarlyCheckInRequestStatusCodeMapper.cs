using IHostPro.Contexts.GuestOperations.Contracts;
using IHostPro.Contexts.GuestOperations.Domain.Enums;

namespace IHostPro.Contexts.GuestOperations.Application;

/// <summary>
/// Maps <see cref="EarlyCheckInRequestStatus"/>/<see cref="EarlyCheckInDenialReason"/>
/// to the stable lowercase codes exposed in <see cref="EarlyCheckInRequestResult"/>
/// — mirrors <see cref="GuestStayOperationStatusCodeMapper"/>'s own explicit-switch
/// convention exactly (Fase 10, Checkpoint 3).
/// </summary>
public static class EarlyCheckInRequestStatusCodeMapper
{
    public static string ToCode(EarlyCheckInRequestStatus status) => status switch
    {
        EarlyCheckInRequestStatus.Pending => "pending",
        EarlyCheckInRequestStatus.Approved => "approved",
        EarlyCheckInRequestStatus.Denied => "denied",
        EarlyCheckInRequestStatus.Cancelled => "cancelled",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unmapped EarlyCheckInRequestStatus."),
    };

    /// <summary>
    /// The exact same reason-code vocabulary published on <see cref="EarlyCheckinDenied"/>
    /// (<see cref="EarlyCheckinDeniedReasonCodes"/>) — one mapping, reused for
    /// both the HTTP response and the Integration Event payload, never two
    /// independent string literals that could silently drift apart.
    /// </summary>
    public static string ToCode(EarlyCheckInDenialReason reason) => reason switch
    {
        EarlyCheckInDenialReason.PolicyNotConfigured => EarlyCheckinDeniedReasonCodes.PolicyNotConfigured,
        EarlyCheckInDenialReason.PolicyNotAllowed => EarlyCheckinDeniedReasonCodes.PolicyNotAllowed,
        EarlyCheckInDenialReason.BeforeEarliestTime => EarlyCheckinDeniedReasonCodes.BeforeEarliestTime,
        EarlyCheckInDenialReason.ScheduleConflict => EarlyCheckinDeniedReasonCodes.ScheduleConflict,
        EarlyCheckInDenialReason.CleaningNotReady => EarlyCheckinDeniedReasonCodes.CleaningNotReady,
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "Unmapped EarlyCheckInDenialReason."),
    };
}
