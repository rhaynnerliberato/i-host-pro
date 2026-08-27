namespace IHostPro.Contexts.PropertyManagement.Contracts;

/// <summary>
/// The single, minimal synchronous query port Communication may use to
/// resolve a Property's current front desk ("Portaria") contact for an
/// operational notification (Fase 10, Checkpoint 4 — ADR-026, synchronous
/// exception #9). "Architecture Principles.md" §14 already names two
/// general-purpose synchronous-query exceptions (Identity &amp; Access,
/// Configuration &amp; Policy) plus several narrow, single-consumer ones
/// (ADR-014, ADR-019, ADR-021, and the two Guest Operations exceptions
/// added by the ADR-024 amendment); this is a NEW, separately named
/// exception, registered by its own ADR-026 — it authorizes only this one
/// query, not a general synchronous-query exception for Property
/// Management. Implemented ONLY in <c>PropertyManagement.Infrastructure</c>
/// — Communication may reference this contract, never
/// <c>PropertyManagement.Application</c>/<c>Infrastructure</c>/<c>Api</c>,
/// and never <c>PropertyManagementDbContext</c>/the
/// <c>property_management</c> schema directly.
///
/// Resolution needs to be synchronous, not an eventually-consistent
/// projection: the send must use the CURRENTLY configured contact — a stale
/// projection could address a message to a contact that has since been
/// disabled or replaced.
/// </summary>
public interface IFrontDeskContactReader
{
    /// <summary>
    /// Resolves internally: Property → CondominiumId → the Condominium's
    /// active <c>FrontDeskContact</c> — the caller never needs to know
    /// <c>CondominiumId</c> or anything about Condominium structure.
    /// Returns <see langword="null"/> when the Property does not exist for
    /// <paramref name="tenantId"/>, when the Property has no Condominium,
    /// or when the Condominium has no active <c>FrontDeskContact</c>
    /// configured — all three are the same, ordinary "nothing to notify"
    /// outcome to the caller, never distinguished (the caller's own
    /// response is always the same deliberate no-op either way).
    /// </summary>
    Task<FrontDeskContactReadResult?> GetActiveByPropertyIdAsync(
        Guid tenantId, Guid propertyId, CancellationToken cancellationToken);
}
