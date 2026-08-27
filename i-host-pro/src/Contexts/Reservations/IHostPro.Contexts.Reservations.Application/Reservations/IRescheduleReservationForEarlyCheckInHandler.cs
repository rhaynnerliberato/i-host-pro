using IHostPro.Contexts.Reservations.Contracts;

namespace IHostPro.Contexts.Reservations.Application.Reservations;

/// <summary>
/// The cross-context command handler contract for
/// <see cref="RescheduleReservationForEarlyCheckIn"/> (Fase 10, Checkpoint
/// 3) — mirrors <see cref="ICloseReservationHandler"/> exactly.
/// </summary>
public interface IRescheduleReservationForEarlyCheckInHandler
{
    Task HandleAsync(RescheduleReservationForEarlyCheckIn command, CancellationToken cancellationToken);
}
