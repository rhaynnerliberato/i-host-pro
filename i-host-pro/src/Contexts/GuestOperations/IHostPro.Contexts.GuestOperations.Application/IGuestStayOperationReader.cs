using IHostPro.Contexts.GuestOperations.Domain;

namespace IHostPro.Contexts.GuestOperations.Application;

/// <summary>
/// Resolves the <see cref="GuestStayOperation"/> owning a given Reservation
/// (Fase 10, Checkpoint 1 — Guest Operations Foundation) — mirrors
/// <c>Reservations.Application.IReservationReader.GetIdByExternalIdentityAsync</c>'s
/// own two-step resolve-then-fetch shape: a command carrying only
/// <c>ReservationId</c> resolves the aggregate's own id here first, then
/// fetches the tracked entity via <c>IRepository&lt;GuestStayOperation, Guid&gt;.GetByIdAsync</c>.
/// </summary>
public interface IGuestStayOperationReader
{
    Task<Guid?> GetIdByReservationIdAsync(Guid reservationId, CancellationToken cancellationToken);
}
