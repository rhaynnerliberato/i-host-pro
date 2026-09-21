using IHostPro.Contexts.Identity.Application;
using Microsoft.EntityFrameworkCore;

namespace IHostPro.Contexts.Identity.Infrastructure.Persistence;

/// <inheritdoc cref="IPasswordResetTokenRepository"/>
public sealed class PasswordResetTokenRepository : IPasswordResetTokenRepository
{
    private readonly IdentityDbContext _dbContext;

    public PasswordResetTokenRepository(IdentityDbContext dbContext) => _dbContext = dbContext;

    public void CreatePending(Guid id, Guid tenantId, Guid userId, string tokenHash, DateTimeOffset createdAtUtc, DateTimeOffset expiresAtUtc)
    {
        _dbContext.PasswordResetTokens.Add(new PasswordResetToken(id, tenantId, userId, tokenHash, createdAtUtc, expiresAtUtc));
    }

    /// <remarks>
    /// A single conditional <c>UPDATE ... RETURNING</c> statement — never a
    /// SELECT followed by a separate UPDATE — is the only way two concurrent
    /// requests presenting the same token can be guaranteed to leave at most
    /// one of them successful. This intentionally bypasses RLS entirely: no
    /// tenant context exists yet at this point.
    /// </remarks>
    public async Task<PasswordResetTokenConsumption?> ConsumeByTokenHashAsync(
        string tokenHash, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        var rows = await _dbContext.Set<PasswordResetTokenConsumptionRow>()
            .FromSqlInterpolated($"""
                UPDATE identity.password_reset_tokens
                SET consumed_at_utc = {nowUtc}
                WHERE token_hash = {tokenHash}
                  AND consumed_at_utc IS NULL
                  AND expires_at_utc > {nowUtc}
                RETURNING
                    tenant_id AS "TenantId",
                    user_id AS "UserId"
                """)
            .ToListAsync(cancellationToken);

        var row = rows.SingleOrDefault();
        return row is null ? null : new PasswordResetTokenConsumption(row.TenantId, row.UserId);
    }
}
