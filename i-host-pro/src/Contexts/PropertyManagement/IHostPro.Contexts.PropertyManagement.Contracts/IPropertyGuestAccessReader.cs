namespace IHostPro.Contexts.PropertyManagement.Contracts;

/// <summary>
/// The single, minimal synchronous query port Communication may use to
/// resolve a Property's current guest access credential/instructions for
/// delivery (Fase 10, Checkpoint 6.2 — Guest Access Secure Delivery
/// Corrective Implementation, CP6.1 Decision Gate). "Architecture
/// Principles.md" §14 already names eleven exceptions; this is a NEW,
/// separately named exception (#12), registered by its own ADR-028 —
/// mirrors <see cref="IFrontDeskContactReader"/>'s own reasoning exactly
/// (ADR-026, exception #9): a strict, purpose-limited read, never a general
/// synchronous-query exception for Property Management. Implemented ONLY in
/// <c>PropertyManagement.Infrastructure</c> — Communication may reference
/// this contract, never <c>PropertyManagement.Application</c>/
/// <c>Infrastructure</c>/<c>Api</c>, and never
/// <c>PropertyManagementDbContext</c>/the <c>property_management</c> schema
/// directly.
///
/// Resolution needs to be synchronous: the credential must reflect the
/// CURRENTLY configured value — a stale projection could deliver a
/// credential the administrator has since changed or deactivated.
/// </summary>
public interface IPropertyGuestAccessReader
{
    /// <summary>
    /// Returns <see langword="null"/> when no ACTIVE
    /// <c>PropertyAccessConfiguration</c> exists for
    /// <paramref name="propertyId"/> under <paramref name="tenantId"/> — a
    /// nonexistent configuration and an inactive one are the same, ordinary
    /// "nothing to deliver" outcome to the caller, never distinguished (same
    /// convention as <see cref="IFrontDeskContactReader"/>'s own collapsed
    /// no-contact cases). When an active configuration exists but its
    /// <c>AccessCredentialSecretReference</c> is set and the underlying
    /// secret cannot be resolved, this throws rather than silently
    /// returning a result with a missing credential — a misconfigured
    /// reference is an infrastructure failure, never swallowed (CP6.2
    /// mandate item 24).
    /// </summary>
    Task<PropertyGuestAccessReadResult?> GetForGuestAccessDeliveryAsync(
        Guid tenantId, Guid propertyId, CancellationToken cancellationToken);
}
