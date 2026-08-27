using IHostPro.BuildingBlocks.Application;

namespace IHostPro.Contexts.GuestOperations.Application;

/// <summary>
/// Requests an early check-in for an existing Reservation (Fase 10,
/// Checkpoint 3). Evaluation is synchronous and automatic — the SAME
/// command creates the request row AND decides it (Approved/Denied) in one
/// unit of work; there is no separate approval endpoint/command. Dispatched
/// through Mediator via <see cref="IGuestOperationsRequestDispatcher"/>,
/// the same universal pattern <see cref="RecordGuestCheckedInCommand"/>
/// already uses.
/// </summary>
public sealed record RequestEarlyCheckInCommand : ICommand<EarlyCheckInRequestResult>
{
    public required Guid TenantId { get; init; }

    public required Guid ReservationId { get; init; }

    /// <summary>The new check-in time the guest is requesting — must be earlier than the Reservation's current <c>CheckInAt</c>.</summary>
    public required DateTimeOffset RequestedCheckInAt { get; init; }
}
