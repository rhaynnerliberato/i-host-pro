namespace IHostPro.Contexts.PropertyManagement.Contracts;

/// <summary>
/// The minimal, opaque result <see cref="IPropertyReservationEligibilityReader"/>
/// returns to any other Bounded Context that needs to know whether a
/// Property can currently receive a reservation (Fase 3, Incremento 1 plan,
/// item 4) — never a full Property projection, never an address, owner, or
/// any other field Reservations has no need for.
/// </summary>
public sealed record PropertyReservationEligibility(
    Guid PropertyId,
    bool IsActive,
    int Capacity);
