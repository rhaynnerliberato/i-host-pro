namespace IHostPro.Contexts.PropertyManagement.Contracts;

/// <summary>
/// The single, minimal synchronous query port another Bounded Context
/// (Reservations) may use to check whether a Property is eligible to
/// receive a new/updated reservation — mirrors
/// <c>Identity.Contracts.IIdentityUserEligibilityReader</c>'s own shape.
/// "Architecture Principles.md" §14 names exactly two general-purpose
/// synchronous-query exceptions (Identity &amp; Access, Configuration &amp;
/// Policy); this specific, narrow query is a third, separately named
/// exception approved by ADR-014 — it authorizes only this one query, not a
/// general synchronous-query exception for Property Management. Implemented
/// ONLY in <c>PropertyManagement.Infrastructure</c> — Reservations may
/// reference this contract, never <c>PropertyManagement.Application</c>/
/// <c>Infrastructure</c>/<c>Api</c>, and never
/// <c>PropertyManagementDbContext</c>/the <c>property_management</c> schema
/// directly.
/// </summary>
public interface IPropertyReservationEligibilityReader
{
    /// <summary>
    /// Returns <c>null</c> when no Property with <paramref name="propertyId"/>
    /// exists for <paramref name="tenantId"/> — a non-existent id and a
    /// cross-tenant id are indistinguishable by design, same convention as
    /// every other cross-context/cross-tenant lookup in this platform.
    /// </summary>
    Task<PropertyReservationEligibility?> GetAsync(
        Guid tenantId,
        Guid propertyId,
        CancellationToken cancellationToken);
}
