using IHostPro.BuildingBlocks.Domain;

namespace IHostPro.Contexts.Reservations.Application.Reservations;

/// <summary>
/// Wraps <see cref="UpdateReservationCommand"/>'s write in this context's
/// transactional outbox executor, translating a caught
/// <c>DbUpdateConcurrencyException</c> into
/// <see cref="Errors.ReservationsErrorCodes.ReservationConcurrencyConflict"/>
/// — mirrors
/// <c>PropertyManagement.Application.Properties.IUpdatePropertyExecutor</c>'s
/// concurrency-only translation (no unique-constraint violation to catch
/// here).
/// </summary>
public interface IUpdateReservationExecutor
{
    Task<Result<ReservationResult>> ExecuteAsync(
        Func<Task<Result<ReservationResult>>> operation, CancellationToken cancellationToken);
}
