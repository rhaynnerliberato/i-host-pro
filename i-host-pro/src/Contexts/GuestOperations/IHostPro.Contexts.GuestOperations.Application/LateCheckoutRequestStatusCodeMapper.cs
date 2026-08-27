using IHostPro.Contexts.GuestOperations.Contracts;
using IHostPro.Contexts.GuestOperations.Domain.Enums;

namespace IHostPro.Contexts.GuestOperations.Application;

/// <summary>
/// Maps <see cref="LateCheckoutRequestStatus"/>/<see cref="LateCheckoutDenialReason"/>/
/// <see cref="LateCheckoutChargeType"/> to the stable lowercase codes exposed
/// in <see cref="LateCheckoutRequestResult"/> — mirrors
/// <see cref="GuestStayOperationStatusCodeMapper"/>'s own explicit-switch
/// convention exactly (Fase 10, Checkpoint 3).
/// </summary>
public static class LateCheckoutRequestStatusCodeMapper
{
    public static string ToCode(LateCheckoutRequestStatus status) => status switch
    {
        LateCheckoutRequestStatus.Pending => "pending",
        LateCheckoutRequestStatus.PendingPayment => "pending_payment",
        LateCheckoutRequestStatus.Approved => "approved",
        LateCheckoutRequestStatus.Denied => "denied",
        LateCheckoutRequestStatus.Cancelled => "cancelled",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unmapped LateCheckoutRequestStatus."),
    };

    /// <summary>
    /// The exact same reason-code vocabulary published on <see cref="LateCheckoutDenied"/>
    /// (<see cref="LateCheckoutDeniedReasonCodes"/>) — one mapping, reused for
    /// both the HTTP response and the Integration Event payload.
    /// </summary>
    public static string ToCode(LateCheckoutDenialReason reason) => reason switch
    {
        LateCheckoutDenialReason.PolicyNotConfigured => LateCheckoutDeniedReasonCodes.PolicyNotConfigured,
        LateCheckoutDenialReason.PolicyNotAllowed => LateCheckoutDeniedReasonCodes.PolicyNotAllowed,
        LateCheckoutDenialReason.AfterLatestTime => LateCheckoutDeniedReasonCodes.AfterLatestTime,
        LateCheckoutDenialReason.ScheduleConflict => LateCheckoutDeniedReasonCodes.ScheduleConflict,
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "Unmapped LateCheckoutDenialReason."),
    };

    /// <summary>
    /// Mirrors <c>Configuration.Contracts.LateCheckoutChargeType</c>'s own
    /// established wire casing (<c>none|fixedAmount|percentage</c> —
    /// <c>PolicyJsonOptions</c>), so this snapshot reads identically to the
    /// policy value it was captured from. <c>Percentage</c> never reaches
    /// this mapper in practice — <see cref="LateCheckoutRequest.Create"/>
    /// rejects it before a row can ever exist — but the branch is kept for
    /// switch exhaustiveness, never silently coalesced into a default case.
    /// </summary>
    public static string ToCode(LateCheckoutChargeType chargeType) => chargeType switch
    {
        LateCheckoutChargeType.None => "none",
        LateCheckoutChargeType.FixedAmount => "fixedAmount",
        LateCheckoutChargeType.Percentage => "percentage",
        _ => throw new ArgumentOutOfRangeException(nameof(chargeType), chargeType, "Unmapped LateCheckoutChargeType."),
    };
}
