using IHostPro.Contexts.Identity.Application;
using IHostPro.Contexts.Identity.Domain;
using Microsoft.EntityFrameworkCore;

namespace IHostPro.Contexts.Identity.Infrastructure.Persistence;

/// <inheritdoc cref="IRefreshTokenReader"/>
public sealed class RefreshTokenReader : IRefreshTokenReader
{
    private readonly IdentityDbContext _dbContext;

    public RefreshTokenReader(IdentityDbContext dbContext) => _dbContext = dbContext;

    public async Task<RefreshToken?> FindByTokenIdAsync(Guid tokenId, CancellationToken cancellationToken) =>
        // No AsNoTracking(): RefreshTokenCommandHandler mutates the result
        // directly via domain methods (MarkRotated) when rotating — it must
        // stay tracked so SaveChangesAsync (owned solely by
        // ITenantAwareUnitOfWork) picks up the change.
        await _dbContext.RefreshTokens.FirstOrDefaultAsync(rt => rt.TokenId == tokenId, cancellationToken);

    public async Task<IReadOnlyCollection<RefreshToken>> FindActiveBySessionIdAsync(
        Guid sessionId, DateTimeOffset now, CancellationToken cancellationToken) =>
        // Same no-AsNoTracking() reasoning as above: LogoutCommandHandler
        // mutates each returned token directly (Revoke).
        await _dbContext.RefreshTokens
            .Where(rt => rt.SessionId == sessionId && rt.RevokedAt == null && rt.ExpiresAt > now)
            .ToListAsync(cancellationToken);
}
