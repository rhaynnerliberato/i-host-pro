using IHostPro.Contexts.Reservations.Domain.Enums;

namespace IHostPro.Contexts.Reservations.Application.Reservations;

/// <summary>
/// Maps <see cref="ReservationSource"/> to the stable lowercase code exposed
/// in the <c>ReservationCreated</c> integration event's payload (Fase 9,
/// Checkpoint 3.2) — mirrors <see cref="ReservationStatusCodeMapper"/>
/// exactly: an explicit switch, never <c>ToString().ToLowerInvariant()</c>.
/// </summary>
public static class ReservationSourceCodeMapper
{
    public static string ToCode(ReservationSource source) => source switch
    {
        ReservationSource.Manual => "manual",
        ReservationSource.Airbnb => "airbnb",
        _ => throw new ArgumentOutOfRangeException(nameof(source), source, "Unmapped ReservationSource."),
    };
}
