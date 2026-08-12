namespace IHostPro.Contexts.Housekeeping.Api.Contracts;

/// <summary>
/// Every field is nullable/default at the wire level — presence is
/// validated by the Application layer (<c>CreateCleaningCommandValidator</c>),
/// never here (mirrors <c>CreateReservationRequest</c>'s convention).
/// <see cref="ReservationId"/> is optional — when supplied, it is validated
/// against this context's own local Reservations projection, never used to
/// derive <see cref="PropertyId"/> (Checkpoint 0 gate — <see cref="PropertyId"/>
/// is always required, regardless of whether a reservation is referenced).
/// No <c>status</c> field: the client can never set it — every cleaning is
/// born <c>Pending</c>. <see cref="ScheduledAtUtc"/> (Incremento 2A) is
/// optional — when omitted, the cleaning simply does not participate in
/// schedule-based ordering until an administrator sets it.
/// </summary>
public sealed record CreateCleaningRequest(Guid? PropertyId, Guid? ReservationId, DateTimeOffset? ScheduledAtUtc = null);
