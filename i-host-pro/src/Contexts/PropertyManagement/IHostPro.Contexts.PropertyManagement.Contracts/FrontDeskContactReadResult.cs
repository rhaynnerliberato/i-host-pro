namespace IHostPro.Contexts.PropertyManagement.Contracts;

/// <summary>
/// The minimal, opaque result <see cref="IFrontDeskContactReader"/> returns
/// to Communication (Fase 10, Checkpoint 4 — ADR-026, synchronous exception
/// #9) — never the <c>FrontDeskContact</c> aggregate itself, never
/// <c>CondominiumId</c>, never any Property/Condominium data. Communication
/// only needs enough to address and identify one operational contact.
/// </summary>
public sealed record FrontDeskContactReadResult(
    Guid ContactId,
    string? DisplayName,
    string PhoneNumber);
