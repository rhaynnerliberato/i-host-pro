namespace IHostPro.Contexts.Reservations.Contracts;

/// <summary>
/// The single, minimal synchronous query port Communication may use to
/// obtain the guest contact data needed to deliver one communication linked
/// to an existing Reservation — mirrors
/// <c>PropertyManagement.Contracts.IPropertyReservationEligibilityReader</c>'s
/// own shape exactly (ADR-014). "Architecture Principles.md" §14 names
/// Identity &amp; Access, Configuration &amp; Policy, and the Reservations →
/// Property Management exception; this specific, narrow query is a fourth,
/// separately named exception approved by ADR-019 — it authorizes only
/// Communication, only this one query, never a general synchronous-query
/// exception for Reservations, and never a precedent for PII in Integration
/// Events. Implemented ONLY in <c>Reservations.Infrastructure</c> —
/// Communication may reference this contract, never
/// <c>Reservations.Domain</c>/<c>Application</c>/<c>Infrastructure</c>, and
/// never <c>ReservationsDbContext</c>/the <c>reservations</c> schema
/// directly.
/// </summary>
public interface IReservationGuestContactReader
{
    /// <summary>
    /// Returns <c>null</c> when no Reservation with <paramref name="reservationId"/>
    /// exists for <paramref name="tenantId"/> — a non-existent id and a
    /// cross-tenant id are indistinguishable by design, same convention as
    /// every other cross-context/cross-tenant lookup in this platform.
    /// </summary>
    Task<ReservationGuestContact?> GetGuestContactAsync(
        Guid tenantId,
        Guid reservationId,
        CancellationToken cancellationToken);
}
