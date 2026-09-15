using IHostPro.Contexts.ExternalIntegrations.Application.AirbnbEmailBridge;
using IHostPro.Contexts.ExternalIntegrations.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IHostPro.Contexts.ExternalIntegrations.Infrastructure.AirbnbEmailBridge;

/// <summary>
/// PostgreSQL-backed <see cref="IAirbnbEmailTokenCacheStore"/>: the
/// plaintext MSAL cache is encrypted via <see cref="ITokenCacheProtector"/>
/// and stored in <c>airbnb_email_mailbox_connections.token_cache_blob</c>.
/// Queries filter by <c>TenantId</c> explicitly (see the interface's own
/// remarks — MSAL's cache-serialization callbacks run outside the ambient
/// tenant-resolution pipeline that <c>BaseDbContext</c>'s Global Query Filter
/// otherwise relies on).
/// </summary>
public sealed class PostgresAirbnbEmailTokenCacheStore : IAirbnbEmailTokenCacheStore
{
    private readonly ExternalIntegrationsDbContext _dbContext;
    private readonly ITokenCacheProtector _protector;
    private readonly TimeProvider _timeProvider;

    public PostgresAirbnbEmailTokenCacheStore(
        ExternalIntegrationsDbContext dbContext, ITokenCacheProtector protector, TimeProvider timeProvider)
    {
        _dbContext = dbContext;
        _protector = protector;
        _timeProvider = timeProvider;
    }

    public async Task<byte[]?> LoadAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var connection = await _dbContext.AirbnbEmailMailboxConnections
            .FirstOrDefaultAsync(c => c.TenantId == tenantId, cancellationToken);

        if (connection?.TokenCacheBlob is null)
            return null;

        return _protector.Unprotect(connection.TokenCacheBlob);
    }

    public async Task SaveAsync(Guid tenantId, byte[] tokenCacheBytes, CancellationToken cancellationToken)
    {
        var connection = await _dbContext.AirbnbEmailMailboxConnections
            .FirstOrDefaultAsync(c => c.TenantId == tenantId, cancellationToken);

        if (connection is null)
        {
            throw new InvalidOperationException(
                $"Cannot save a token cache for tenant {tenantId:D} - no Airbnb Email Bridge mailbox connection exists yet.");
        }

        connection.UpdateTokenCache(_protector.Protect(tokenCacheBytes), _timeProvider.GetUtcNow());

        // Intentionally not caught: a DbUpdateConcurrencyException here means
        // another writer (the OAuth callback, or a concurrent Worker poll)
        // already updated this same cache — the caller must reload and
        // retry, never silently overwrite (Fase 9 review item 7).
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task ClearAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var connection = await _dbContext.AirbnbEmailMailboxConnections
            .FirstOrDefaultAsync(c => c.TenantId == tenantId, cancellationToken);

        if (connection is null)
            return;

        connection.Disconnect(_timeProvider.GetUtcNow());
        await _dbContext.SaveChangesAsync(cancellationToken);
    }
}
