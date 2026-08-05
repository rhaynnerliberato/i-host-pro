using IHostPro.Contexts.Reservations.Domain.Enums;

namespace IHostPro.Contexts.Reservations.Application.Reservations;

/// <summary>
/// Maps <see cref="ReservationStatus"/> to the stable lowercase code exposed
/// in <see cref="ReservationResult"/> and in the <c>ReservationCreated</c>
/// integration event's payload — mirrors
/// <c>PropertyManagement.Application.Properties.PropertyStatusCodeMapper</c>
/// exactly: an explicit switch, not <c>ToString().ToLowerInvariant()</c>, so
/// the exposed contract never silently changes if the enum's member names
/// are ever refactored.
/// </summary>
public static class ReservationStatusCodeMapper
{
    public static string ToCode(ReservationStatus status) => status switch
    {
        ReservationStatus.Confirmed => "confirmed",
        ReservationStatus.Cancelled => "cancelled",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unmapped ReservationStatus."),
    };

    /// <summary>
    /// The inverse of <see cref="ToCode"/> — used by <c>IReservationReader</c>'s
    /// listing filter to translate <c>ListReservationsQuery.Status</c> (already
    /// validated by <c>ListReservationsQueryValidator</c> to be one of the two
    /// stable codes) back into the enum EF Core queries against.
    /// </summary>
    public static ReservationStatus FromCode(string code) => code switch
    {
        "confirmed" => ReservationStatus.Confirmed,
        "cancelled" => ReservationStatus.Cancelled,
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, "Unmapped reservation status code."),
    };
}
