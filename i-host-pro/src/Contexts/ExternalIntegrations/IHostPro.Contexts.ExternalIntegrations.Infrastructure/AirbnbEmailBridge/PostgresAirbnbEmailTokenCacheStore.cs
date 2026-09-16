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
/// otherwise relies on). That explicit filter only replaces the EF-side
/// predicate, though: the underlying table is still RLS-protected, so every
/// access here also runs inside its own <see cref="IAirbnbEmailUnitOfWork"/>
/// transaction to establish the <c>app.tenant_id</c> session setting RLS
/// requires — without it, PostgreSQL silently hides the row regardless of
/// this class's own <c>TenantId</c> predicate.
/// </summary>
public sealed class PostgresAirbnbEmailTokenCacheStore : IAirbnbEmailTokenCacheStore
{
    private readonly ExternalIntegrationsDbContext _dbContext;
    private readonly IAirbnbEmailUnitOfWork _unitOfWork;
    private readonly ITokenCacheProtector _protector;
    private readonly TimeProvider _timeProvider;

    public PostgresAirbnbEmailTokenCacheStore(
        ExternalIntegrationsDbContext dbContext,
        IAirbnbEmailUnitOfWork unitOfWork,
        ITokenCacheProtector protector,
        TimeProvider timeProvider)
    {
        _dbContext = dbContext;
        _unitOfWork = unitOfWork;
        _protector = protector;
        _timeProvider = timeProvider;
    }

    public async Task<byte[]?> LoadAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var blob = await _unitOfWork.ExecuteAsync(async () =>
        {
            var connection = await _dbContext.AirbnbEmailMailboxConnections
                .FirstOrDefaultAsync(c => c.TenantId == tenantId, cancellationToken);
            return connection?.TokenCacheBlob;
        }, cancellationToken);

        return blob is null ? null : _protector.Unprotect(blob);
    }

    public Task SaveAsync(Guid tenantId, byte[] tokenCacheBytes, CancellationToken cancellationToken) =>
        _unitOfWork.ExecuteAsync(async () =>
        {
            var connection = await _dbContext.AirbnbEmailMailboxConnections
                .FirstOrDefaultAsync(c => c.TenantId == tenantId, cancellationToken);

            if (connection is null)
            {
                throw new InvalidOperationException(
                    $"Cannot save a token cache for tenant {tenantId:D} - no Airbnb Email Bridge mailbox connection exists yet.");
            }

            // Intentionally not caught: a DbUpdateConcurrencyException here
            // means another writer (the OAuth callback, or a concurrent
            // Worker poll) already updated this same cache — the caller must
            // reload and retry, never silently overwrite (Fase 9 review item 7).
            connection.UpdateTokenCache(_protector.Protect(tokenCacheBytes), _timeProvider.GetUtcNow());
            return true;
        }, cancellationToken);

    public Task ClearAsync(Guid tenantId, CancellationToken cancellationToken) =>
        _unitOfWork.ExecuteAsync(async () =>
        {
            var connection = await _dbContext.AirbnbEmailMailboxConnections
                .FirstOrDefaultAsync(c => c.TenantId == tenantId, cancellationToken);

            connection?.Disconnect(_timeProvider.GetUtcNow());
            return true;
        }, cancellationToken);
}
