using IHostPro.Contexts.Reservations.Domain.Enums;

namespace IHostPro.Contexts.Reservations.Application.Reservations;

/// <summary>
/// The minimal, read-only projection <see cref="IReservationReader.GetUpdateSnapshotAsync"/>
/// returns to <c>UpdateReservationCommandHandler</c> — just enough to
/// compute a prospective PropertyId/schedule/guest count and to reject an
/// already-<see cref="ReservationStatus.Cancelled"/> reservation, BEFORE the
/// write transaction opens (Fase 3 §3 transactional-order correction).
/// <see cref="Xmin"/> is the row's PostgreSQL optimistic-concurrency token
/// at the instant of this read — compared later, inside the write
/// transaction, against <see cref="IReservationReader.GetCurrentXminAsync"/>'s
/// result to detect whether the row changed in between.
/// </summary>
public sealed record ReservationUpdateSnapshot(
    Guid Id,
    Guid PropertyId,
    DateTimeOffset CheckInAt,
    DateTimeOffset CheckOutAt,
    int GuestCount,
    ReservationStatus Status,
    uint Xmin);
