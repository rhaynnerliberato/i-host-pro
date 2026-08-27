using IHostPro.BuildingBlocks.Application;

namespace IHostPro.Contexts.GuestOperations.Application;

/// <summary>
/// Requests a late checkout for an existing Reservation (Fase 10, Checkpoint
/// 3). Evaluation is synchronous and automatic, mirroring
/// <see cref="RequestEarlyCheckInCommand"/> exactly, with one exception: when
/// the effective policy requires PIX confirmation, the request settles at
/// <c>PendingPayment</c> instead of a final decision (Fase 10, Checkpoint 5
/// closes that loop). Dispatched through Mediator via
/// <see cref="IGuestOperationsRequestDispatcher"/>.
/// </summary>
public sealed record RequestLateCheckoutCommand : ICommand<LateCheckoutRequestResult>
{
    public required Guid TenantId { get; init; }

    public required Guid ReservationId { get; init; }

    /// <summary>The new checkout time the guest is requesting — must be later than the Reservation's current <c>CheckOutAt</c>.</summary>
    public required DateTimeOffset RequestedCheckOutAt { get; init; }
}
