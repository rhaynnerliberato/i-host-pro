namespace IHostPro.Contexts.GuestOperations.Application;

/// <summary>
/// Handler contract for <see cref="RecordGuestCheckedOutCommand"/> — mirrors
/// <c>Housekeeping.Application.ICreateCleaningForReservationHandler</c>'s own
/// shape.
/// </summary>
public interface IRecordGuestCheckedOutHandler
{
    Task HandleAsync(RecordGuestCheckedOutCommand command, CancellationToken cancellationToken);
}
