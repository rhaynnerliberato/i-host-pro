using IHostPro.BuildingBlocks.Application;

namespace IHostPro.Contexts.GuestOperations.Application;

/// <summary>
/// Requests that a guest's access credential/instructions be delivered
/// (Fase 10, Checkpoint 6.2 — Guest Access Secure Delivery Corrective
/// Implementation). An explicit operational action — never automatic, never
/// scheduled. Dispatched through Mediator via
/// <see cref="IGuestOperationsRequestDispatcher"/> — mirrors
/// <c>RecordGuestCheckedInCommand</c>'s own established shape.
/// </summary>
public sealed record RequestGuestAccessDeliveryCommand : ICommand<GuestStayOperationResult>
{
    public required Guid TenantId { get; init; }

    public required Guid ReservationId { get; init; }
}
