using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.Identity.Contracts;
using IHostPro.Contexts.Identity.Domain;
using IHostPro.Contexts.Identity.Domain.Enums;

namespace IHostPro.Contexts.Identity.Application;

/// <summary>
/// Revokes the caller's own session and every refresh token still active
/// under it (Incremento 2 plan, Etapa 11). Runs inside the transaction
/// <c>TenantTransactionBehavior</c> opens for this (non-bootstrap) command —
/// never calls <c>SaveChangesAsync</c> itself; every mutation below is staged
/// only, persisted atomically once by <c>ITenantAwareUnitOfWork.ExecuteAsync</c>
/// at the end of the pipeline.
///
/// <see cref="LogoutCommand.TenantId"/>/<see cref="LogoutCommand.UserId"/>/
/// <see cref="LogoutCommand.SessionId"/> come exclusively from the
/// authenticated access token's claims (see <see cref="LogoutCommand"/>) —
/// there is no untrusted input to validate here beyond non-emptiness
/// (<see cref="LogoutCommandValidator"/>).
///
/// A session that does not exist, belongs to a different user, or is
/// already revoked all fold into the exact same idempotent success: this
/// handler never reveals whether a session existed or any detail of its
/// internal state, and a repeated logout call is always safe to retry.
/// Tenant isolation is enforced by Row-Level Security on the query itself
/// (the same `app.tenant_id` <see cref="LogoutCommand.TenantId"/> was
/// derived from) — a session belonging to a different tenant is simply
/// invisible to the query, indistinguishable from not existing at all.
/// </summary>
public sealed class LogoutCommandHandler : ICommandHandler<LogoutCommand>
{
    /// <summary>
    /// Session-scoped, free-form revocation reason (unlike
    /// <see cref="RefreshToken"/>'s enum-typed <c>RevocationReason</c>) —
    /// named after <see cref="RefreshTokenRevocationReason.LogoutRequested"/>
    /// so both records of "why" trace back to the same vocabulary.
    /// </summary>
    private static readonly string SessionLogoutRevocationReason =
        nameof(RefreshTokenRevocationReason.LogoutRequested);

    private readonly IRepository<Session, Guid> _sessionRepository;
    private readonly IRefreshTokenReader _refreshTokenReader;
    private readonly ISecurityAuditWriter _auditWriter;
    private readonly IIntegrationEventCollector _eventCollector;
    private readonly ISessionRevocationSignal _revocationSignal;
    private readonly TimeProvider _timeProvider;

    public LogoutCommandHandler(
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

    public async ValueTask<Result> Handle(LogoutCommand command, CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();

        var session = await _sessionRepository.GetByIdAsync(command.SessionId, cancellationToken);

        if (session is null || session.UserId != command.UserId || session.Status != SessionStatus.Active)
            return Result.Success();

        // Session is the source of truth for the whole rotation chain's
        // revocation (a future refresh checks Session.Status first) — the
        // per-token Revoke calls below exist so each token's OWN record also
        // shows why it stopped being usable, for a reader who inspects
        // `refresh_tokens` directly without cross-referencing the session.
        var activeTokens = await _refreshTokenReader.FindActiveBySessionIdAsync(session.Id, now, cancellationToken);
        foreach (var token in activeTokens)
            token.Revoke(RefreshTokenRevocationReason.LogoutRequested, now);

        session.Revoke(SessionLogoutRevocationReason, now);

        // Staged for the executor to write to ISessionRevocationCache AFTER
        // this transaction actually commits — never from inside it
        // (Incremento 2 plan, Etapa 12; see ISessionRevocationSignal).
        _revocationSignal.MarkRevoked(command.TenantId, session.Id);

        var correlationId = Guid.NewGuid();

        var entry = SecurityAuditEntry.Record(
            Guid.NewGuid(), command.TenantId, SecurityAuditEventType.LogoutSucceeded, now, correlationId,
            reasonCode: null, userId: command.UserId, sessionId: session.Id, refreshTokenId: null, ipAddress: null);
        _auditWriter.Record(entry);

        var userLoggedOut = new UserLoggedOut
        {
            TenantId = command.TenantId,
            AggregateId = command.UserId,
            AggregateType = "User",
            CorrelationId = correlationId,
            ActorType = "User",
            ActorId = command.UserId.ToString(),
            SessionId = session.Id,
        };
        _eventCollector.Enqueue(userLoggedOut);

        _eventCollector.Enqueue(new SessionRevoked
        {
            TenantId = command.TenantId,
            AggregateId = command.UserId,
            AggregateType = "User",
            CorrelationId = correlationId,
            CausationId = userLoggedOut.EventId,
            ActorType = "User",
            ActorId = command.UserId.ToString(),
            SessionId = session.Id,
            ReasonCode = SessionRevokedReasonCodes.LogoutRequested,
        });

        return Result.Success();
    }
}
