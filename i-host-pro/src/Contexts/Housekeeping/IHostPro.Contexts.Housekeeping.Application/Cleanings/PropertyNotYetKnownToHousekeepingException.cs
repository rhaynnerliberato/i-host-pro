namespace IHostPro.Contexts.Housekeeping.Application.Cleanings;

/// <summary>
/// Thrown by <see cref="CreateCleaningForReservationCommandHandler"/> when
/// <see cref="IHostPro.Contexts.Housekeeping.Contracts.CreateCleaningForReservation"/> arrives for a
/// property Housekeeping's own local projection does not yet know as active
/// — a potentially transient condition caused by a race between Property
/// Management's <c>PropertyActivated</c> propagation and Workflow
/// Orchestration's <c>ReservationCreated</c>→<c>CreateCleaningForReservation</c>
/// chain (both fired independently, with independent delivery latency), never
/// a permanent classification by itself.
///
/// Deliberately its own type, never the generic <see cref="InvalidOperationException"/>
/// this handler originally used (Housekeeping Workflow Command Retry/Redelivery
/// Production Gate): Wolverine's own bounded-retry policy
/// (<c>CreateCleaningForReservationHandler.Configure</c>) is scoped to exactly
/// this exception type — an unrelated bug elsewhere in this handler throwing a
/// generic <see cref="InvalidOperationException"/> must never accidentally
/// receive the same retry treatment. Mirrors
/// <c>WhatsAppMessageNotYetAvailableException</c>'s own precedent exactly.
/// </summary>
public sealed class PropertyNotYetKnownToHousekeepingException : Exception
{
    public PropertyNotYetKnownToHousekeepingException(string message) : base(message)
    {
    }
}
