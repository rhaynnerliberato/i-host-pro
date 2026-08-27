namespace IHostPro.Contexts.GuestOperations.Domain.Enums;

/// <summary>
/// A <see cref="GuestOperations.GuestStayOperation"/>'s business-state
/// lifecycle (Fase 10, Checkpoint 1 — Guest Operations Foundation;
/// Checkpoint 2 — Check-in/Checkout Core). Every
/// <see cref="GuestOperations.GuestStayOperation"/> is born <see cref="Active"/>;
/// <see cref="CheckedIn"/> represents the guest's real arrival/entry
/// granted; <see cref="CheckedOut"/> is terminal — no restoration exists.
/// Only <c>Active → CheckedIn → CheckedOut</c> is a valid path — checkout
/// from <see cref="Active"/> directly is an invariant violation (a checkout
/// without a recorded check-in represents an operational inconsistency,
/// per the user's own Checkpoint 2 decision).
///
/// Documento 10's fuller check-in granularity (awaiting form, awaiting
/// contact, access delivered, instructions sent, front desk notified) are
/// deliberately NOT modeled as states here — the Checkpoint 2 Decision Gate
/// classified them as process steps/future capabilities, never persistent
/// business states this aggregate needs to represent.
/// </summary>
public enum GuestStayOperationStatus
{
    Active = 0,
    CheckedIn = 1,
    CheckedOut = 2,
}
