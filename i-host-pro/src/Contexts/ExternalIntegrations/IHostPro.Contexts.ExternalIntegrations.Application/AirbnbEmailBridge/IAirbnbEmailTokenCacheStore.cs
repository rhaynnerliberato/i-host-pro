namespace IHostPro.Contexts.ExternalIntegrations.Application.AirbnbEmailBridge;

/// <summary>
/// Persists the MSAL (Microsoft.Identity.Client) token-cache serialization
/// for a tenant's Airbnb Email Bridge mailbox connection. Deliberately narrow
/// — no mailbox-provider HTTP behavior, no OAuth flow orchestration here,
/// only load/save/clear of the opaque cache bytes MSAL itself produces and
/// consumes. Named around the business capability, not the mailbox provider
/// SDK, to keep this Application-layer contract provider-neutral (mirrors
/// this Bounded Context's own Meta-vocabulary isolation rule — see
/// <c>ExternalIntegrationsDependencyTests.Meta_Named_Types_Only_Exist_In_The_Infrastructure_Meta_Namespace</c>,
/// whose forbidden-substring list also catches the mailbox provider's own
/// product name). Encryption-at-rest is an implementation detail of whatever
/// backs this interface (currently PostgreSQL + AES-GCM); callers only ever
/// see plaintext MSAL cache bytes.
///
/// Tenant scoping is explicit here (unlike most repositories in this
/// context) because MSAL's own cache-serialization callbacks run outside the
/// ambient request pipeline that normally establishes <c>ITenantContext</c>.
/// </summary>
public interface IAirbnbEmailTokenCacheStore
{
    /// <returns>The plaintext MSAL cache bytes, or <c>null</c> if the tenant has no connection yet, or the connection has no stored cache (never connected, or disconnected).</returns>
    Task<byte[]?> LoadAsync(Guid tenantId, CancellationToken cancellationToken);

    /// <summary>
    /// Persists an MSAL-produced cache update for a tenant that already has a
    /// mailbox connection (created via the OAuth callback flow — a separate,
    /// later gate). Throws <see cref="InvalidOperationException"/> if no
    /// connection exists yet for the tenant.
    /// </summary>
    /// <exception cref="Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException">
    /// Thrown, never swallowed, if another writer (the OAuth callback or a
    /// concurrent Worker poll) updated the same connection's cache first —
    /// callers must reload and retry rather than assume last-write-wins.
    /// </exception>
    Task SaveAsync(Guid tenantId, byte[] tokenCacheBytes, CancellationToken cancellationToken);

    /// <summary>Disconnects the tenant's mailbox connection and discards its stored token cache. A no-op if the tenant has no connection.</summary>
    Task ClearAsync(Guid tenantId, CancellationToken cancellationToken);
}
