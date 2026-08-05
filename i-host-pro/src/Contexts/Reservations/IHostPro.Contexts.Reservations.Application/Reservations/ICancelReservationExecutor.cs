using IHostPro.BuildingBlocks.Domain;

namespace IHostPro.Contexts.Reservations.Application.Reservations;

/// <summary>
/// Wraps <see cref="CancelReservationCommand"/>'s write in this context's
/// transactional outbox executor, translating a caught
/// <c>DbUpdateConcurrencyException</c> into
/// <see cref="Errors.ReservationsErrorCodes.ReservationConcurrencyConflict"/>
/// — mirrors <see cref="IUpdateReservationExecutor"/> exactly. No advisory
/// lock: cancelling never touches the check-in/check-out interval, so it
/// cannot race the date-conflict check.
/// </summary>
public interface ICancelReservationExecutor
{
    Task<Result<ReservationResult>> ExecuteAsync(
        Func<Task<Result<ReservationResult>>> operation, CancellationToken cancellationToken);
}
