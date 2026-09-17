using IHostPro.Contexts.ExternalIntegrations.Application.AirbnbEmailBridge;
using Microsoft.EntityFrameworkCore;

namespace IHostPro.Contexts.ExternalIntegrations.Infrastructure.Persistence;

/// <inheritdoc cref="IAirbnbEmailOAuthTransactionRepository"/>
public sealed class AirbnbEmailOAuthTransactionRepository : IAirbnbEmailOAuthTransactionRepository
{
    private readonly ExternalIntegrationsDbContext _dbContext;

    public AirbnbEmailOAuthTransactionRepository(ExternalIntegrationsDbContext dbContext) => _dbContext = dbContext;

    public void CreatePending(
        Guid id, Guid tenantId, Guid actorUserId, string stateHash,
        byte[] protectedPkceVerifier, DateTimeOffset createdAtUtc, DateTimeOffset expiresAtUtc)
    {
        _dbContext.AirbnbEmailOAuthTransactions.Add(
            new AirbnbEmailOAuthTransaction(id, tenantId, actorUserId, stateHash, protectedPkceVerifier, createdAtUtc, expiresAtUtc));
    }

    /// <remarks>
    /// A single conditional <c>UPDATE ... RETURNING</c> statement — never a
    /// SELECT followed by a separate UPDATE — is the only way two concurrent
    /// callbacks presenting the same state can be guaranteed to leave at most
    /// one of them successful (Web OAuth architecture gate, item 15-16). This
    /// intentionally bypasses <c>IAirbnbEmailUnitOfWork</c>/RLS entirely: no
    /// tenant context exists yet at this point in the callback.
    /// </remarks>
    public async Task<AirbnbEmailOAuthTransactionConsumption?> ConsumeByStateHashAsync(
        string stateHash, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        var rows = await _dbContext.Set<AirbnbEmailOAuthTransactionConsumptionRow>()
            .FromSqlInterpolated($"""
                UPDATE external_integrations.airbnb_email_oauth_transactions
                SET consumed_at_utc = {nowUtc}
                WHERE state_hash = {stateHash}
                  AND consumed_at_utc IS NULL
                  AND expires_at_utc > {nowUtc}
                RETURNING
                    tenant_id AS "TenantId",
                    actor_user_id AS "ActorUserId",
                    protected_pkce_verifier AS "ProtectedPkceVerifier"
                """)
            .ToListAsync(cancellationToken);

        var row = rows.SingleOrDefault();
        return row is null
            ? null
            : new AirbnbEmailOAuthTransactionConsumption(row.TenantId, row.ActorUserId, row.ProtectedPkceVerifier);
    }
}
