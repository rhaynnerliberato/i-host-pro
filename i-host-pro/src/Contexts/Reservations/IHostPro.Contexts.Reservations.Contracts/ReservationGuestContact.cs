namespace IHostPro.Contexts.Reservations.Contracts;

/// <summary>
/// The minimal, opaque result <see cref="IReservationGuestContactReader"/>
/// returns to Communication (Fase 9, Checkpoint 1 — ADR-019) — never a full
/// Reservation projection, never the reservation's value, dates, status,
/// property, address, documents, or <c>GuestCount</c>. <see cref="GuestName"/>
/// is deliberately absent — no template this checkpoint requires it; adding
/// it later is its own decision, never a silent "just in case" addition.
/// <see cref="GuestPhone"/> is nullable because <c>Reservation.GuestPhone</c>
/// itself is optional (Fase 3) — a resolved Reservation with no phone on
/// file is a real, distinct case from "Reservation not found" (<c>null</c>
/// result), never conflated by this contract.
/// </summary>
public sealed record ReservationGuestContact(
    Guid ReservationId,
    string? GuestPhone);
