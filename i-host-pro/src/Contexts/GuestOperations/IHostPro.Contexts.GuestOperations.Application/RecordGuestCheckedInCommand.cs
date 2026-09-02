using IHostPro.BuildingBlocks.Application;

namespace IHostPro.Contexts.GuestOperations.Application;

/// <summary>
/// Records a guest's real check-in for an existing <c>GuestStayOperation</c>
/// (Fase 10, Checkpoint 2 — Check-in/Checkout Core). Dispatched through
/// Mediator via <see cref="IGuestOperationsRequestDispatcher"/> — mirrors
/// <c>CreateReservationCommand</c>'s own established shape, the universal
/// pattern every other HTTP-exposed command in this codebase uses.
///
/// Fase 12, Checkpoint 4 (Security/Secrets/LGPD Hardening) — unlike
/// <see cref="RequestGuestAccessDeliveryCommand"/>, this command has exactly
/// one real caller (an administrator via <c>GuestStayOperationsController</c>
/// — no AI Tool triggers it), so <see cref="ActorId"/> alone is enough; the
/// handler always publishes <c>ActorType = "User"</c>.
/// </summary>
public sealed record RecordGuestCheckedInCommand : ICommand<GuestStayOperationResult>
{
    public required Guid TenantId { get; init; }

    public required Guid ReservationId { get; init; }

    public required Guid ActorId { get; init; }
}
