namespace IHostPro.Contexts.ExternalIntegrations.Infrastructure.Persistence;

/// <summary>
/// Infrastructure-only "pre-authentication security state" row (Web OAuth
/// architecture gate, item 13) — deliberately NOT a Domain aggregate and
/// deliberately NOT <c>ITenantOwned</c>: at <c>oauth/callback</c> time the
/// tenant is not yet known, and <c>BaseDbContext</c> would otherwise apply an
/// automatic <c>TenantId == ITenantContext.TenantId</c> Global Query Filter
/// that evaluates to "no rows" whenever the tenant is unresolved — exactly
/// the circular dependency the architecture gate corrected. No RLS policy is
/// created for this table's migration for the same reason. Its safety comes
/// from <see cref="Application.AirbnbEmailBridge.IAirbnbEmailOAuthTransactionRepository"/>'s
/// narrow surface, not from row-level access control.
/// </summary>
public sealed class AirbnbEmailOAuthTransaction
{
    public Guid Id { get; private set; }
    public string StateHash { get; private set; } = null!;
    public Guid TenantId { get; private set; }
    public Guid ActorUserId { get; private set; }
    public byte[] ProtectedPkceVerifier { get; private set; } = null!;
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset ExpiresAtUtc { get; private set; }
    public DateTimeOffset? ConsumedAtUtc { get; private set; }

    private AirbnbEmailOAuthTransaction()
    {
        // EF Core materialization.
    }

    public AirbnbEmailOAuthTransaction(
        Guid id, Guid tenantId, Guid actorUserId, string stateHash,
        byte[] protectedPkceVerifier, DateTimeOffset createdAtUtc, DateTimeOffset expiresAtUtc)
    {
        Id = id;
        TenantId = tenantId;
        ActorUserId = actorUserId;
        StateHash = stateHash;
        ProtectedPkceVerifier = protectedPkceVerifier;
        CreatedAtUtc = createdAtUtc;
        ExpiresAtUtc = expiresAtUtc;
    }
}
