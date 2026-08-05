using IHostPro.BuildingBlocks.Application;

namespace IHostPro.Contexts.Reservations.Application.Reservations;

/// <summary>
/// Cancels a reservation (Fase 3, Incremento 1 plan, item 8) —
/// <c>Confirmed</c> → <c>Cancelled</c>, terminal, no restoration exists this
/// increment. No body — the route-bound <see cref="ReservationId"/> is
/// everything the request needs. Cancelling releases the reservation's
/// interval immediately (a <c>Cancelled</c> reservation never blocks the
/// date-conflict check).
/// </summary>
public sealed record CancelReservationCommand(
    Guid TenantId,
    Guid ActorId,
    Guid ReservationId) : ICommand<ReservationResult>;
