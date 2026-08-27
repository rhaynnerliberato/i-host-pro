namespace IHostPro.Contexts.GuestOperations.Domain.Enums;

/// <summary>
/// An Early Check-in request's lifecycle (Fase 10, Checkpoint 3). Every
/// request is born <see cref="Pending"/> — deliberately never persisted in
/// that state in practice: the deciding command handler evaluates policy/
/// schedule/cleaning readiness synchronously, in the SAME unit of work that
/// creates the row, and transitions it to <see cref="Approved"/>/
/// <see cref="Denied"/> before the transaction commits (Documento 10's own
/// flow has no manual/asynchronous approval step). <see cref="Cancelled"/>
/// is reserved for a future guest-initiated withdrawal, not used by any
/// Checkpoint 3 flow yet. All three non-<see cref="Pending"/> values are
/// terminal — no restoration exists.
/// </summary>
public enum EarlyCheckInRequestStatus
{
    Pending = 0,
    Approved = 1,
    Denied = 2,
    Cancelled = 3,
}
