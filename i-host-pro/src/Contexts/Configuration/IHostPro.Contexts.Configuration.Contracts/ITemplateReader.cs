namespace IHostPro.Contexts.Configuration.Contracts;

/// <summary>
/// The single, typed synchronous query port another Bounded Context may use
/// to resolve the active Template for a given key — reuses the general
/// Configuration &amp; Policy synchronous-query exception already named in
/// "Architecture Principles.md" §14 (Exceção 1, ADR-002); no new ADR
/// required, unlike ADR-014/ADR-019's own narrow, single-consumer
/// exceptions. Implemented ONLY in <c>Configuration.Infrastructure</c> — a
/// consumer may reference this contract, never
/// <c>Configuration.Application</c>/<c>Infrastructure</c>/<c>Api</c>, and
/// never <c>ConfigurationDbContext</c>/the <c>configuration</c> schema
/// directly.
/// </summary>
public interface ITemplateReader
{
    /// <summary>
    /// Returns <c>null</c> when no active Template exists for
    /// <paramref name="key"/> under <paramref name="tenantId"/> — mirrors
    /// <c>IEarlyCheckInPolicyReader.GetEffectiveAsync</c>'s own shape
    /// (explicit <c>tenantId</c>, never the ambient <c>ITenantContext</c> a
    /// Wolverine consumer's own ADR-016 boundary may have resolved for a
    /// different purpose).
    /// </summary>
    Task<ActiveTemplate?> GetActiveByKeyAsync(Guid tenantId, string key, CancellationToken cancellationToken);
}
