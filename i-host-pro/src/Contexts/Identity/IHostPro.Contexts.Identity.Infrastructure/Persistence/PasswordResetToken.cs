namespace IHostPro.Contexts.Identity.Infrastructure.Persistence;

/// <summary>
/// Infrastructure-only "pre-authentication security state" row (Self-Service
/// Identity &amp; Onboarding Foundation gate) — deliberately NOT a Domain
/// aggregate and deliberately NOT <c>ITenantOwned</c>: at
/// <c>forgot-password/complete</c> time the tenant is not yet known, and
/// <c>BaseDbContext</c> would otherwise apply an automatic
/// <c>TenantId == ITenantContext.TenantId</c> Global Query Filter that
/// evaluates to "no rows" whenever the tenant is unresolved — the exact same
/// circularity <c>AirbnbEmailOAuthTransaction</c> (Web OAuth architecture
/// gate) already resolved this way. No RLS policy exists for this table's
/// migration for the same reason. Its safety comes from
/// <see cref="Application.IPasswordResetTokenRepository"/>'s narrow surface,
/// not from row-level access control.
/// </summary>
public sealed class PasswordResetToken
{
    public Guid Id { get; private set; }
    public string TokenHash { get; private set; } = null!;
    public Guid TenantId { get; private set; }
    public Guid UserId { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset ExpiresAtUtc { get; private set; }
    public DateTimeOffset? ConsumedAtUtc { get; private set; }

    private PasswordResetToken()
    {
        // EF Core materialization.
    }

    public PasswordResetToken(
        Guid id, Guid tenantId, Guid userId, string tokenHash, DateTimeOffset createdAtUtc, DateTimeOffset expiresAtUtc)
    {
        Id = id;
        TenantId = tenantId;
        UserId = userId;
        TokenHash = tokenHash;
        CreatedAtUtc = createdAtUtc;
        ExpiresAtUtc = expiresAtUtc;
    }
}
