namespace IHostPro.Contexts.Reservations.Contracts;

/// <summary>
/// The minimal, opaque result <see cref="IReservationGuestContactReader"/>
/// returns to Communication (Fase 9, Checkpoint 1 — ADR-019) — never a full
/// Reservation projection, never the reservation's value, dates, status,
/// property, address, documents, or <c>GuestCount</c>.
/// <see cref="GuestPhone"/> is nullable because <c>Reservation.GuestPhone</c>
/// itself is optional (Fase 3) — a resolved Reservation with no phone on
/// file is a real, distinct case from "Reservation not found" (<c>null</c>
/// result), never conflated by this contract.
///
/// <see cref="GuestName"/> was added in Fase 10, Checkpoint 4 (Portaria
/// Notification Foundation) — Communication's new Front Desk processors
/// need the guest's name to render an operational notification to the
/// front desk. ADR-019 item 4 explicitly anticipated this exact scenario
/// and required an explicit decision before extending this DTO, never a
/// silent "just in case" addition — that decision is recorded as ADR-019's
/// own factual-extension note (the purpose/consumer/scope boundary itself
/// is unchanged: still exactly Communication, still exactly one
/// communication tied to an existing Reservation). <see cref="GuestPhone"/>
/// is never included in a Front Desk notification — that remains
/// explicitly out of scope (Fase 10, Checkpoint 4 mandate). Non-nullable
/// because <c>Reservation.GuestName</c> itself is required (Fase 3) — a
/// resolved Reservation always has one.
/// </summary>
public sealed record ReservationGuestContact(
    Guid ReservationId,
    string? GuestPhone,
    string GuestName);
