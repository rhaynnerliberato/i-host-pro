using IHostPro.Contexts.Identity.Application;
using IHostPro.Contexts.Identity.Application.Sessions;
using IHostPro.Contexts.Identity.Domain.Enums;

namespace IHostPro.Contexts.Identity.Infrastructure.Sessions;

/// <inheritdoc cref="IUserSessionRevoker"/>
/// <remarks>
/// Built entirely from already-existing capabilities — <see cref="ISessionReader.ListActiveForUpdateByUserIdAsync"/>
/// (find the target sessions, TRACKED — never the read-only
/// <see cref="ISessionReader.ListActiveByUserIdAsync"/>, since this class
/// mutates every returned instance), <see cref="IRefreshTokenReader"/> (find
/// each session's still-active refresh tokens) and <see cref="ISessionRevocationSignal"/>
/// (stage the post-commit cache write) — never touches <see cref="IdentityDbContext"/>
/// directly. This mirrors exactly what <c>RevokeOwnSessionCommandHandler</c>
/// already does for its single session, generalized to every active session
/// of a user (Incremento 3, Checkpoint 6).
/// </remarks>
public sealed class UserSessionRevoker : IUserSessionRevoker
{
    private readonly ISessionReader _sessionReader;
    private readonly IRefreshTokenReader _refreshTokenReader;
    private readonly ISessionRevocationSignal _revocationSignal;

    public UserSessionRevoker(
        ISessionReader sessionReader, IRefreshTokenReader refreshTokenReader, ISessionRevocationSignal revocationSignal)
    {
        _sessionReader = sessionReader;
        _refreshTokenReader = refreshTokenReader;
        _revocationSignal = revocationSignal;
    }

    public async Task<IReadOnlyCollection<Guid>> RevokeAllActiveSessionsAsync(
        Guid tenantId,
        Guid userId,
        string sessionRevocationReasonCode,
        RefreshTokenRevocationReason refreshTokenRevocationReason,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var activeSessions = await _sessionReader.ListActiveForUpdateByUserIdAsync(userId, cancellationToken);
        if (activeSessions.Count == 0)
            return [];

        var revokedSessionIds = new List<Guid>(activeSessions.Count);

        foreach (var session in activeSessions)
        {
            var activeTokens = await _refreshTokenReader.FindActiveBySessionIdAsync(session.Id, now, cancellationToken);
            foreach (var token in activeTokens)
                token.Revoke(refreshTokenRevocationReason, now);

            session.Revoke(sessionRevocationReasonCode, now);

            // Staged for the caller's executor to write to
            // ISessionRevocationCache AFTER the transaction actually commits
            // — never from inside it (Incremento 2 plan, Etapa 12).
            _revocationSignal.MarkRevoked(tenantId, session.Id);

            revokedSessionIds.Add(session.Id);
        }

        return revokedSessionIds;
    }
}
