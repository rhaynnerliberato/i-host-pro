using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.Identity.Application.Errors;
using IHostPro.Contexts.Identity.Contracts;
using IHostPro.Contexts.Identity.Domain;
using IHostPro.Contexts.Identity.Domain.Enums;

namespace IHostPro.Contexts.Identity.Application.Sessions;

/// <summary>
/// Revokes one of the authenticated caller's own sessions and every refresh
/// token still active under it (Incremento 3, Checkpoint 4). Runs inside the
/// transaction <see cref="IRevokeOwnSessionExecutor"/> opens for this command
/// — never calls <c>SaveChangesAsync</c> itself; every mutation below is
/// staged only.
///
/// Unlike <c>LogoutCommandHandler</c> — whose <see cref="RevokeOwnSessionCommand.SessionId"/>
/// equivalent always comes from the caller's OWN access token, so
/// "nonexistent/foreign/inactive" can never legitimately happen — this
/// command's <see cref="RevokeOwnSessionCommand.SessionId"/> is a
/// client-supplied route value that could name ANY session. A session that
/// does not exist, belongs to a different user, belongs to a different
/// tenant (invisible by construction — Row-Level Security/the Global Query
/// Filter on the query itself, same reasoning as <c>LogoutCommandHandler</c>),
/// or is already revoked all fold into the exact same external outcome
/// (<see cref="IdentityErrorCodes.SessionNotOwnedByUser"/>) — this handler
/// never reveals which specific reason applied, to avoid a session-id
/// enumeration oracle (Incremento 3, Checkpoint 4, approved design). That
/// path performs absolutely no side effect: no event, no refresh token
/// revocation, no revocation-cache write, no audit entry.
/// </summary>
public sealed class RevokeOwnSessionCommandHandler : ICommandHandler<RevokeOwnSessionCommand>
{
    private static readonly Error SessionNotOwnedByUserError = new(
        IdentityErrorCodes.SessionNotOwnedByUser, IdentityErrorCodes.SessionNotOwnedByUser);

    private readonly IRepository<Session, Guid> _sessionRepository;
    private readonly IRefreshTokenReader _refreshTokenReader;
    private readonly ISecurityAuditWriter _auditWriter;
    private readonly IIntegrationEventCollector _eventCollector;
    private readonly ISessionRevocationSignal _revocationSignal;
    private readonly TimeProvider _timeProvider;

    public RevokeOwnSessionCommandHandler(
        IRepository<Session, Guid> sessionRepository,
        IRefreshTokenReader refreshTokenReader,
        ISecurityAuditWriter auditWriter,
        IIntegrationEventCollector eventCollector,
        ISessionRevocationSignal revocationSignal,
        TimeProvider timeProvider)
    {
        _sessionRepository = sessionRepository;
        _refreshTokenReader = refreshTokenReader;
        _auditWriter = auditWriter;
        _eventCollector = eventCollector;
        _revocationSignal = revocationSignal;
        _timeProvider = timeProvider;
    }

    public async ValueTask<Result> Handle(RevokeOwnSessionCommand command, CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();

        var session = await _sessionRepository.GetByIdAsync(command.SessionId, cancellationToken);

        if (session is null || session.UserId != command.UserId || session.Status != SessionStatus.Active)
            return Result.Failure(SessionNotOwnedByUserError);

        var activeTokens = await _refreshTokenReader.FindActiveBySessionIdAsync(session.Id, now, cancellationToken);
        foreach (var token in activeTokens)
            token.Revoke(RefreshTokenRevocationReason.UserRequestedRevocation, now);

        session.Revoke(SessionRevokedReasonCodes.UserRequestedRevocation, now);

        // Staged for the executor to write to ISessionRevocationCache AFTER
        // this transaction actually commits — never from inside it
        // (Incremento 2 plan, Etapa 12; see ISessionRevocationSignal).
        _revocationSignal.MarkRevoked(command.TenantId, session.Id);

        var correlationId = Guid.NewGuid();

        var entry = SecurityAuditEntry.Record(
            Guid.NewGuid(), command.TenantId, SecurityAuditEventType.SessionRevoked, now, correlationId,
            reasonCode: null, userId: command.UserId, sessionId: session.Id, refreshTokenId: null, ipAddress: null);
        _auditWriter.Record(entry);

        // Exactly one event for the whole session — never one per refresh
        // token (Incremento 3, Checkpoint 4, Section 5). No CausationId: unlike
        // Logout (UserLoggedOut) or the refresh-token-reuse cascade
        // (RefreshTokenReuseDetected), there is no other event published in
        // this same transaction for this one to be caused by.
        _eventCollector.Enqueue(new SessionRevoked
        {
            TenantId = command.TenantId,
            AggregateId = command.UserId,
            AggregateType = "User",
            CorrelationId = correlationId,
            ActorType = "User",
            ActorId = command.UserId.ToString(),
            SessionId = session.Id,
            ReasonCode = SessionRevokedReasonCodes.UserRequestedRevocation,
        });

        return Result.Success();
    }
}
