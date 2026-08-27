using IHostPro.Contexts.GuestOperations.Domain.Enums;

namespace IHostPro.Contexts.GuestOperations.Application;

/// <summary>
/// Maps <see cref="GuestStayOperationStatus"/> to the stable lowercase code
/// exposed in <see cref="GuestStayOperationResult"/> — mirrors
/// <c>Reservations.Application.Reservations.ReservationStatusCodeMapper</c>
/// exactly: an explicit switch, not <c>ToString().ToLowerInvariant()</c>, so
/// the exposed contract never silently changes if the enum's member names
/// are ever refactored.
/// </summary>
public static class GuestStayOperationStatusCodeMapper
{
    public static string ToCode(GuestStayOperationStatus status) => status switch
    {
        GuestStayOperationStatus.Active => "active",
        GuestStayOperationStatus.CheckedIn => "checked_in",
        GuestStayOperationStatus.CheckedOut => "checked_out",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unmapped GuestStayOperationStatus."),
    };
}
