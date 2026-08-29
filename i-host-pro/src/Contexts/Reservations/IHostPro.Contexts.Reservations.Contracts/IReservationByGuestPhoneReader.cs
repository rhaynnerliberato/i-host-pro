namespace IHostPro.Contexts.Reservations.Contracts;

/// <summary>
/// The single, minimal synchronous query port Communication may use to
/// resolve which Reservation(s) an inbound guest message's phone number
/// could belong to — Architecture Principles.md §14's thirteenth named
/// synchronous exception (ADR-029, Fase 11 Checkpoint 1 — Inbound
/// Conversation Foundation). Deliberately NOT an extension of
/// <see cref="IReservationGuestContactReader"/> (ADR-019, Exceção 5): that
/// contract goes <c>ReservationId → GuestPhone</c> for an already-known
/// Reservation; this one goes the opposite direction,
/// <c>GuestPhoneNormalized → Reservation candidate(s)</c>, with no
/// Reservation identity known yet. Same pair of Bounded Contexts, distinct
/// purpose and distinct contract — mirrors the ADR-019/ADR-026 vs.
/// ADR-028/Exceção 12 precedent exactly.
///
/// Implemented ONLY in <c>Reservations.Infrastructure</c> — Communication may
/// reference this contract, never <c>Reservations.Domain</c>/
/// <c>Application</c>/<c>Infrastructure</c>, and never
/// <c>ReservationsDbContext</c>/the <c>reservations</c> schema directly.
/// </summary>
public interface IReservationByGuestPhoneReader
{
    /// <summary>
    /// Returns every <see cref="Domain.Enums.ReservationStatus.Confirmed"/>
    /// Reservation for <paramref name="tenantId"/> whose stored
    /// <c>GuestPhone</c> matches <paramref name="guestPhoneNormalized"/> once
    /// both sides are reduced to digits-only — an empty list means either no
    /// match or no eligible Reservation exists; the two are indistinguishable
    /// by design, same convention as every other cross-context lookup in
    /// this platform. <see cref="Domain.Enums.ReservationStatus.Cancelled"/>
    /// and <c>Closed</c> are never eligible (ADR-029) — this is the sole
    /// lifecycle filter, with deliberately no temporal window on top of it.
    /// </summary>
    Task<IReadOnlyList<ReservationCandidate>> FindEligibleByGuestPhoneAsync(
        Guid tenantId,
        string guestPhoneNormalized,
        CancellationToken cancellationToken);
}

/// <summary>
/// Purpose-limited projection returned by
/// <see cref="IReservationByGuestPhoneReader"/> — deliberately excludes
/// <c>GuestName</c>/<c>GuestPhone</c>/any administrative status, payment
/// data, or access credential. If a genuinely new field becomes necessary,
/// widening this contract requires its own governance decision (ADR-029),
/// never a silent addition.
/// </summary>
public sealed record ReservationCandidate(
    Guid ReservationId,
    Guid PropertyId,
    DateTimeOffset CheckInAt,
    DateTimeOffset CheckOutAt);
