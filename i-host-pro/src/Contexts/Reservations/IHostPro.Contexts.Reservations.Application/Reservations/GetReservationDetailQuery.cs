using IHostPro.BuildingBlocks.Application;

namespace IHostPro.Contexts.Reservations.Application.Reservations;

/// <summary>
/// Reads a single reservation's full administrative detail (Fase 3,
/// Incremento 1 plan, item 9) — mirrors <c>GetPropertyDetailQuery</c>.
/// Nonexistent/cross-tenant ids are indistinguishable by design (both
/// <c>404</c> via <see cref="Errors.ReservationsErrorCodes.ReservationNotFound"/>).
/// </summary>
public sealed record GetReservationDetailQuery(Guid ReservationId) : IQuery<ReservationResult>;
