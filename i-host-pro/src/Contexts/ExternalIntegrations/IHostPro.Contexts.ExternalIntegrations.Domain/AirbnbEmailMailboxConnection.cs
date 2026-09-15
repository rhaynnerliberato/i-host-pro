using IHostPro.BuildingBlocks.Domain;

namespace IHostPro.Contexts.ExternalIntegrations.Domain;

/// <summary>
/// A tenant's connection to a Microsoft Graph mailbox used as the Airbnb
/// Email Bridge's discovery channel (temporary bridge connector — ADR-023's
/// "Airbnb Deterministic Foundation" is the real reservation-import target
/// this feeds; the bridge only supplies the transport/discovery mechanism).
/// One mailbox per tenant is an explicit MVP constraint (unique index on
/// <see cref="TenantId"/>), not a generalized multi-mailbox model.
///
/// <see cref="TokenCacheBlob"/> holds an opaque, encrypted MSAL
/// (Microsoft.Identity.Client) token-cache serialization — this entity never
/// models an access/refresh token itself; MSAL owns the token lifecycle
/// entirely. <see cref="HomeAccountId"/> (not <see cref="MailboxAddress"/>)
/// is the stable identifier MSAL needs to locate the cached account, since a
/// mailbox address alone is not a reliable account key.
/// </summary>
public sealed class AirbnbEmailMailboxConnection : AggregateRoot<Guid>, ITenantOwned
{
    public Guid TenantId { get; private set; }
    public string? MailboxAddress { get; private set; }
    public string? HomeAccountId { get; private set; }
    public string? AccountTenantId { get; private set; }
    public bool IsEnabled { get; private set; }
    public string? GrantedScopes { get; private set; }
    public byte[]? TokenCacheBlob { get; private set; }
    public AirbnbEmailAuthorizationStatus AuthorizationStatus { get; private set; }
    public DateTimeOffset? LastAuthenticatedAtUtc { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset? UpdatedAtUtc { get; private set; }

    private AirbnbEmailMailboxConnection()
    {
        // EF Core materialization.
    }

    private AirbnbEmailMailboxConnection(Guid id, Guid tenantId, DateTimeOffset createdAtUtc) : base(id)
    {
        TenantId = tenantId;
        IsEnabled = false;
        AuthorizationStatus = AirbnbEmailAuthorizationStatus.Disconnected;
        CreatedAtUtc = createdAtUtc;
    }

    public static AirbnbEmailMailboxConnection Create(Guid id, Guid tenantId, DateTimeOffset createdAtUtc) =>
        new(id, tenantId, createdAtUtc);

    /// <summary>
    /// Records a successful interactive consent — used both for the first
    /// connection and for a later re-authorization (Fase 9 review item 22:
    /// the resulting state transition is identical either way).
    /// </summary>
    public void Connect(
        string homeAccountId, string? accountTenantId, string? mailboxAddress, string? grantedScopes,
        byte[] tokenCacheBlob, DateTimeOffset connectedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(homeAccountId))
            throw new ArgumentException("Home account id cannot be empty.", nameof(homeAccountId));
        if (tokenCacheBlob is null || tokenCacheBlob.Length == 0)
            throw new ArgumentException("Token cache blob cannot be empty.", nameof(tokenCacheBlob));

        HomeAccountId = homeAccountId;
        AccountTenantId = accountTenantId;
        MailboxAddress = mailboxAddress;
        GrantedScopes = grantedScopes;
        TokenCacheBlob = tokenCacheBlob;
        AuthorizationStatus = AirbnbEmailAuthorizationStatus.Connected;
        IsEnabled = true;
        LastAuthenticatedAtUtc = connectedAtUtc;
        UpdatedAtUtc = connectedAtUtc;
    }

    /// <summary>
    /// Persists an MSAL-refreshed token cache after a silent token
    /// acquisition. Both the OAuth callback and the Worker's background
    /// polling can call this concurrently — optimistic concurrency via the
    /// mapped <c>xmin</c> column is what protects against a lost update here.
    /// </summary>
    public void UpdateTokenCache(byte[] tokenCacheBlob, DateTimeOffset updatedAtUtc)
    {
        if (tokenCacheBlob is null || tokenCacheBlob.Length == 0)
            throw new ArgumentException("Token cache blob cannot be empty.", nameof(tokenCacheBlob));

        TokenCacheBlob = tokenCacheBlob;
        UpdatedAtUtc = updatedAtUtc;
    }

    /// <summary>
    /// Marks the connection unusable after a token refresh that MSAL could
    /// not silently recover from — requires the tenant to reconnect
    /// interactively. Does not clear the token cache blob (kept for
    /// diagnostics) or any receipt/sync history.
    /// </summary>
    public void MarkError(DateTimeOffset errorAtUtc)
    {
        AuthorizationStatus = AirbnbEmailAuthorizationStatus.Error;
        UpdatedAtUtc = errorAtUtc;
    }

    /// <summary>
    /// Disables the connection and clears the encrypted token cache. Retains
    /// <see cref="HomeAccountId"/>/<see cref="MailboxAddress"/> and every
    /// receipt/sync-state row as historical record (Fase 9 review item 21) —
    /// only a fresh <see cref="Connect"/> reactivates it.
    /// </summary>
    public void Disconnect(DateTimeOffset disconnectedAtUtc)
    {
        TokenCacheBlob = null;
        GrantedScopes = null;
        IsEnabled = false;
        AuthorizationStatus = AirbnbEmailAuthorizationStatus.Disconnected;
        UpdatedAtUtc = disconnectedAtUtc;
    }
}
