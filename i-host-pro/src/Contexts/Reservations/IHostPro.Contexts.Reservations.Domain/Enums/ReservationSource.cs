namespace IHostPro.Contexts.Reservations.Domain.Enums;

/// <summary>
/// Where a Reservation originated (Fase 9, Checkpoint 3.2 — "Airbnb
/// Deterministic Foundation"). Provider-neutral by design — <see cref="Airbnb"/>
/// is the only external channel this checkpoint adds; no
/// Booking/VRBO/Expedia value exists yet (CP3.2 mandate, item E: "Não
/// adicionar Booking/VRBO/etc.").
/// </summary>
public enum ReservationSource
{
    Manual = 0,
    Airbnb = 1,
}
