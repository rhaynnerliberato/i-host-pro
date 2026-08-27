using IHostPro.BuildingBlocks.Application;

namespace IHostPro.Contexts.GuestOperations.Application;

/// <summary>
/// Records a guest's real check-in for an existing <c>GuestStayOperation</c>
/// (Fase 10, Checkpoint 2 — Check-in/Checkout Core). Dispatched through
/// Mediator via <see cref="IGuestOperationsRequestDispatcher"/> — mirrors
/// <c>CreateReservationCommand</c>'s own established shape, the universal
/// pattern every other HTTP-exposed command in this codebase uses.
/// </summary>
public sealed record RecordGuestCheckedInCommand : ICommand<GuestStayOperationResult>
{
    public required Guid TenantId { get; init; }

    public required Guid ReservationId { get; init; }
}
