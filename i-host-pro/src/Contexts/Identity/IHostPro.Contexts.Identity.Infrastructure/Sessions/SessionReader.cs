using IHostPro.Contexts.Identity.Application.Sessions;
using IHostPro.Contexts.Identity.Domain;
using IHostPro.Contexts.Identity.Domain.Enums;
using IHostPro.Contexts.Identity.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IHostPro.Contexts.Identity.Infrastructure.Sessions;

/// <inheritdoc cref="ISessionReader"/>
public sealed class SessionReader : ISessionReader
{
    private readonly IdentityDbContext _dbContext;

    public SessionReader(IdentityDbContext dbContext) => _dbContext = dbContext;

    public async Task<IReadOnlyCollection<Session>> ListActiveByUserIdAsync(Guid userId, CancellationToken cancellationToken) =>
        // AsNoTracking(): a pure listing for ListOwnSessionsQueryHandler — no
        // caller mutates any of these instances. See ISessionReader's doc
        // comment (Incremento 3, Checkpoint 9 follow-up review) for why this
        // is a separate method from ListActiveForUpdateByUserIdAsync rather
        // than a single method with a tracking flag.
        await _dbContext.Sessions
            .AsNoTracking()
            .Where(session => session.UserId == userId && session.Status == SessionStatus.Active)
            .ToListAsync(cancellationToken);

    /// <inheritdoc cref="ISessionReader.ListActiveForUpdateByUserIdAsync"/>
    public async Task<IReadOnlyCollection<Session>> ListActiveForUpdateByUserIdAsync(Guid userId, CancellationToken cancellationToken) =>
        // No AsNoTracking(): UserSessionRevoker mutates every returned Session
        // directly via Revoke() — see ISessionReader's doc comment for the bug
        // this method name exists to prevent from recurring.
        await _dbContext.Sessions
            .Where(session => session.UserId == userId && session.Status == SessionStatus.Active)
            .ToListAsync(cancellationToken);
}
