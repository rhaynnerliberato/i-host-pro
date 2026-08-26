namespace IHostPro.Contexts.GuestOperations.Application;

/// <summary>
/// Records a guest's real checkout for an existing <c>GuestStayOperation</c>
/// (Fase 10, Checkpoint 1 — Guest Operations Foundation). Deliberately a
/// plain Application-layer command, never routed through Mediator/HTTP — CP1
/// has zero public API endpoints; this is resolved and invoked directly (via
/// <see cref="IRecordGuestCheckedOutHandler"/>), mirroring how
/// <c>Housekeeping.Application.ICreateCleaningForReservationHandler</c> is
/// resolved directly rather than through a generic command bus.
/// </summary>
public sealed record RecordGuestCheckedOutCommand
{
    public required Guid TenantId { get; init; }

    public required Guid ReservationId { get; init; }
}
