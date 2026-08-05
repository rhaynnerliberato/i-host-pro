using IHostPro.BuildingBlocks.Application;

namespace IHostPro.Contexts.Reservations.Application.Reservations;

/// <summary>
/// The two read-only administrative reservation queries (Fase 3, Incremento
/// 1 plan, item 9) — mirrors
/// <c>PropertyManagement.Application.Properties.IPropertyReader</c>'s shape.
/// </summary>
public interface IReservationReader
{
    /// <summary>
    /// <paramref name="pageSize"/> is defensively clamped to the fixed
    /// maximum (100) by the implementation itself — never trusts the caller
    /// alone. Ordered deterministically by <c>checkInAt</c> then <c>id</c>
    /// (Fase 3, Incremento 1 plan, item 9). <paramref name="from"/>/
    /// <paramref name="to"/>, when supplied, return reservations whose
    /// interval intersects <c>[from, to)</c> — same half-open convention as
    /// the conflict check (item 7).
    /// </summary>
    Task<PagedResult<ReservationSummaryResult>> ListAsync(
        Guid? propertyId, string? status, DateTimeOffset? from, DateTimeOffset? to,
        int page, int pageSize, CancellationToken cancellationToken);

    Task<ReservationResult?> GetByIdAsync(Guid reservationId, CancellationToken cancellationToken);

    /// <summary>
    /// <see cref="ReservationUpdateSnapshot"/>'s own read — <c>null</c> when
    /// no reservation with <paramref name="reservationId"/> exists for the
    /// current tenant (Global Query Filter already scopes this, same
    /// convention as <see cref="GetByIdAsync"/>). A plain, non-transactional
    /// query — its connection returns to the pool as soon as it completes,
    /// never held open across the caller's later write transaction.
    /// </summary>
    Task<ReservationUpdateSnapshot?> GetUpdateSnapshotAsync(Guid reservationId, CancellationToken cancellationToken);

    /// <summary>
    /// The row's CURRENT PostgreSQL <c>xmin</c> system column value, read
    /// fresh — <c>null</c> when no reservation with <paramref name="reservationId"/>
    /// exists for the current tenant. Called from inside an already-open
    /// write transaction to compare against an earlier
    /// <see cref="ReservationUpdateSnapshot.Xmin"/>, detecting whether the
    /// row changed since that snapshot was taken (Fase 3 §3).
    /// </summary>
    Task<uint?> GetCurrentXminAsync(Guid reservationId, CancellationToken cancellationToken);
}
