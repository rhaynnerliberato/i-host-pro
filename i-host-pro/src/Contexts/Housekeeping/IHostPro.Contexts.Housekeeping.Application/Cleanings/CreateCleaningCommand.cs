using IHostPro.BuildingBlocks.Application;

namespace IHostPro.Contexts.Housekeeping.Application.Cleanings;

/// <summary>
/// Creates a new cleaning, born <c>Pending</c> (Fase 6, Incremento 1) —
/// always administrative/manual (Checkpoint 0 gate: never auto-derived from
/// a Reservation's checkout, that trigger belongs to Fase 10).
/// <see cref="TenantId"/>/<see cref="ActorId"/> come exclusively from the
/// authenticated Administrator/Operator's access token claims — a
/// controller builds this from <c>ClaimsPrincipal</c>, never from the
/// request body. <see cref="PropertyId"/> is always caller-supplied
/// (approved decision, Checkpoint 0 gate) — never derived from
/// <see cref="ReservationId"/>. <see cref="ReservationId"/> is optional;
/// when supplied, it is validated against this context's own local
/// Reservations projection, never used to look up <see cref="PropertyId"/>.
/// </summary>
public sealed record CreateCleaningCommand(
    Guid TenantId,
    Guid ActorId,
    Guid PropertyId,
    Guid? ReservationId) : ICommand<CleaningResult>;
