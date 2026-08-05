using IHostPro.BuildingBlocks.Domain;

namespace IHostPro.Contexts.Reservations.Application.Reservations;

/// <summary>
/// Wraps <see cref="CreateReservationCommand"/>'s write in this context's
/// transactional outbox executor — mirrors
/// <c>PropertyManagement.Application.Properties.ICreatePropertyExecutor</c>'s
/// shape. Unlike Property (which translates a unique-code violation here),
/// Reservations has no database-level uniqueness constraint to catch: the
/// date-conflict rule is enforced entirely in application code, under
/// <see cref="IReservationConflictGuard"/>'s advisory lock, inside
/// <paramref name="operation"/> itself.
/// </summary>
public interface ICreateReservationExecutor
{
    Task<Result<ReservationResult>> ExecuteAsync(
        Func<Task<Result<ReservationResult>>> operation, CancellationToken cancellationToken);
}
