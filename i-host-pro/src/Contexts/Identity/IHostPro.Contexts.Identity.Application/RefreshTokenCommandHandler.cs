using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.Identity.Contracts;
using IHostPro.Contexts.Identity.Domain;
using IHostPro.Contexts.Identity.Domain.Enums;

namespace IHostPro.Contexts.Identity.Application;

/// <summary>
/// Exchanges a presented refresh token for a new access/refresh token pair,
/// rotating the chain (Incremento 2 plan, Etapa 10). Runs entirely inside the
/// transaction opened for this <see cref="RefreshTokenCommand"/>
/// (<c>IBootstrapRequest</c>) — never calls <c>SaveChangesAsync</c> itself;
/// every mutation is staged only, persisted atomically once by
/// <c>ITenantAwareUnitOfWork.ExecuteAsync</c> at the end of the pipeline. A
/// genuine optimistic-concurrency race on the presented token's row (two
/// concurrent refresh attempts) surfaces there too, as a
/// <c>DbUpdateConcurrencyException</c> — the Unit of Work retries by
/// re-running this handler from scratch against reloaded data, so this class
/// needs no concurrency-specific code of its own: the same classification
/// logic below naturally reclassifies the token's now up-to-date state.
///
/// Every rejection path — malformed token or unresolved/inactive tenant
/// (handled in <see cref="RefreshTokenTenantBootstrapResolver"/>, before this
/// handler ever runs), token not found, hash mismatch, revoked (for any
/// reason), expired, inactive user, inactive session — returns the exact
/// same <see cref="Error"/> (<see cref="InvalidRefreshTokenError"/>): the
/// specific reason is recorded only internally, in
/// <c>security_audit_log</c>, never surfaced to the caller.
///
/// Never logs, and never includes in any <see cref="Error"/>,
/// <see cref="RefreshTokenCommand.RefreshToken"/>, the new access token, the
/// new refresh token, or any token hash.
/// </summary>
public sealed class RefreshTokenCommandHandler : ICommandHandler<RefreshTokenCommand, AuthTokensResult>
{
    private static readonly Error InvalidRefreshTokenError = new("refresh_token_invalid", "refresh_token_invalid");

    /// <summary>
    /// Session-scoped, non-persisted revocation reason recorded on
    /// <c>Session.RevocationReason</c> (a free-form string, unlike
    /// <c>RefreshToken.RevocationReason</c>'s enum) when reuse of an
    /// already-rotated token is detected. The presented token's OWN
    /// <c>RevocationReason</c> is deliberately left untouched — it already
    /// correctly says <c>Rotated</c>, which is the true reason it stopped
    /// being valid; reuse is a fact about this new attempt, not about why the
    /// old token itself was revoked.
    /// </summary>
    private const string ReuseDetectedSessionRevocationReason = "refresh_token_reuse_detected";

    private const int MaxIpAddressLength = 45; // matches SessionConfiguration (Incremento 1)

    private readonly IRefreshTokenParser _parser;
    private readonly IRefreshTokenReader _refreshTokenReader;
    private readonly IRefreshTokenHasher _hasher;
    private readonly IRefreshTokenGenerator _refreshTokenGenerator;
    private readonly IRefreshTokenRotationPolicy _rotationPolicy;
    private readonly IRepository<RefreshToken, Guid> _refreshTokenRepository;
    private readonly IRepository<Session, Guid> _sessionRepository;
    private readonly IUserAuthenticationService _userAuthenticationService;
    private readonly IUserRoleReader _roleReader;
    private readonly IJwtTokenGenerator _jwtTokenGenerator;
    private readonly ISecurityAuditWriter _auditWriter;
    private readonly IIntegrationEventCollector _eventCollector;
    private readonly ISessionRevocationSignal _revocationSignal;
    private readonly ICurrentTenantProvider _currentTenantProvider;
    private readonly TimeProvider _timeProvider;

    public RefreshTokenCommandHandler(
        IRefreshTokenParser parser,
        IRefreshTokenReader refreshTokenReader,
        IRefreshTokenHasher hasher,
        IRefreshTokenGenerator refreshTokenGenerator,
        IRefreshTokenRotationPolicy rotationPolicy,
        IRepository<RefreshToken, Guid> refreshTokenRepository,
        IRepository<Session, Guid> sessionRepository,
        IUserAuthenticationService userAuthenticationService,
        IUserRoleReader roleReader,
        IJwtTokenGenerator jwtTokenGenerator,
        ISecurityAuditWriter auditWriter,
        IIntegrationEventCollector eventCollector,
        ISessionRevocationSignal revocationSignal,
        ICurrentTenantProvider currentTenantProvider,
        TimeProvider timeProvider)
    {
        _parser = parser;
        _refreshTokenReader = refreshTokenReader;
        _hasher = hasher;
        _refreshTokenGenerator = refreshTokenGenerator;
        _rotationPolicy = rotationPolicy;
        _refreshTokenRepository = refreshTokenRepository;
        _sessionRepository = sessionRepository;
        _userAuthenticationService = userAuthenticationService;
        _roleReader = roleReader;
        _jwtTokenGenerator = jwtTokenGenerator;
        _auditWriter = auditWriter;
        _eventCollector = eventCollector;
        _revocationSignal = revocationSignal;
        _currentTenantProvider = currentTenantProvider;
        _timeProvider = timeProvider;
    }

    public async ValueTask<Result<AuthTokensResult>> Handle(RefreshTokenCommand command, CancellationToken cancellationToken)
    {
        var tenantId = _currentTenantProvider.TenantId;
        var now = _timeProvider.GetUtcNow();
        var correlationId = Guid.NewGuid();
        var ipAddress = Truncate(command.RequestContext.IpAddress, MaxIpAddressLength);

        // RefreshTokenTenantBootstrapResolver already required this exact
        // string to parse successfully before this handler could ever run —
        // re-parsing here is cheap and deterministic (it will succeed
        // identically); this is a defensive fallback, not a path exercised
        // in production, since a parse failure never resolves a tenant.
        if (!_parser.TryParse(command.RefreshToken, out var parsed))
            return Failure();

        var token = await _refreshTokenReader.FindByTokenIdAsync(parsed.TokenId, cancellationToken);
        if (token is null)
        {
            // Deliberately the same rejection reason as a hash mismatch
            // below: from the caller's perspective (and even internally,
            // for audit purposes) "no stored token matches this presented
            // string" is a single category, whether because the selector
            // does not exist at all or because it does but the secret is
            // wrong.
            return Reject(
                SecurityAuditEventType.RefreshRejected, SecurityAuditReasonCode.RefreshTokenHashMismatch,
                tenantId, now, correlationId, ipAddress, userId: null, sessionId: null, refreshTokenId: parsed.TokenId);
        }

        // The full presented string is hashed and compared in fixed time —
        // never just the secret segment — before anything about the token's
        // state is inspected, so a wrong secret for a real TokenId learns
        // nothing about that token's revocation/expiry status.
        if (!_hasher.Verify(command.RefreshToken, token.TokenHash))
        {
            return Reject(
                SecurityAuditEventType.RefreshRejected, SecurityAuditReasonCode.RefreshTokenHashMismatch,
                tenantId, now, correlationId, ipAddress, token.UserId, token.SessionId, token.TokenId);
        }

        if (token.IsRevoked)
        {
            return token.RevocationReason!.Value switch
            {
                RefreshTokenRevocationReason.Rotated => await HandleRotatedTokenPresentedAgainAsync(
                    token, tenantId, now, correlationId, ipAddress, cancellationToken),

                RefreshTokenRevocationReason.Expired => Reject(
                    SecurityAuditEventType.RefreshRejected, SecurityAuditReasonCode.RefreshTokenExpired,
                    tenantId, now, correlationId, ipAddress, token.UserId, token.SessionId, token.TokenId),

                RefreshTokenRevocationReason.LogoutRequested => Reject(
                    SecurityAuditEventType.RefreshRejected, SecurityAuditReasonCode.LogoutRequested,
                    tenantId, now, correlationId, ipAddress, token.UserId, token.SessionId, token.TokenId),

                RefreshTokenRevocationReason.AdminRevoked => Reject(
                    SecurityAuditEventType.RefreshRejected, SecurityAuditReasonCode.AdminRevoked,
                    tenantId, now, correlationId, ipAddress, token.UserId, token.SessionId, token.TokenId),

                // RefreshTokenRevocationReason.ReuseDetected or any future
                // member: this handler never sets this value itself (reuse
                // is recorded on the Session, not the token — see
                // ReuseDetectedSessionRevocationReason above), but a token
                // found in this state is unambiguously reuse-related.
                _ => Reject(
                    SecurityAuditEventType.RefreshTokenReuseDetected, reasonCode: null,
                    tenantId, now, correlationId, ipAddress, token.UserId, token.SessionId, token.TokenId),
            };
        }

        if (token.IsExpired(now))
        {
            return Reject(
                SecurityAuditEventType.RefreshRejected, SecurityAuditReasonCode.RefreshTokenExpired,
                tenantId, now, correlationId, ipAddress, token.UserId, token.SessionId, token.TokenId);
        }

        var user = await _userAuthenticationService.FindByIdAsync(token.UserId);
        if (user is null || user.Status != UserStatus.Active)
        {
            return Reject(
                SecurityAuditEventType.RefreshRejected, SecurityAuditReasonCode.UserBlocked,
                tenantId, now, correlationId, ipAddress, token.UserId, token.SessionId, token.TokenId);
        }

        var session = await _sessionRepository.GetByIdAsync(token.SessionId, cancellationToken);
        if (session is null || session.Status != SessionStatus.Active)
        {
            return Reject(
                SecurityAuditEventType.RefreshRejected, SecurityAuditReasonCode.SessionNotActive,
                tenantId, now, correlationId, ipAddress, token.UserId, token.SessionId, token.TokenId);
        }

        // ---- Everything validated: rotate ----
        var generatedRefreshToken = _refreshTokenGenerator.Generate(tenantId);
        token.MarkRotated(generatedRefreshToken.TokenId, now);

        var newToken = RefreshToken.Issue(
            Guid.NewGuid(), generatedRefreshToken.TokenId, tenantId, session.Id, user.Id,
            generatedRefreshToken.TokenHash, now, generatedRefreshToken.ExpiresAt, previousTokenId: token.TokenId);
        _refreshTokenRepository.Add(newToken);

        session.Touch(now);

        var roles = await _roleReader.GetRoleCodesAsync(user.Id, cancellationToken);
        var accessToken = _jwtTokenGenerator.GenerateAccessToken(
            new JwtAccessTokenRequest(user.Id, tenantId, session.Id, roles));

        RecordAudit(tenantId, now, correlationId, SecurityAuditEventType.RefreshSucceeded,
            reasonCode: null, user.Id, ipAddress, session.Id, generatedRefreshToken.TokenId);

        var result = new AuthTokensResult(
            accessToken.Token, accessToken.ExpiresAt, generatedRefreshToken.Token, generatedRefreshToken.ExpiresAt);
        return Result.Success(result);
    }

    /// <summary>
    /// The presented token was already rotated (consumed by an earlier,
    /// successful refresh). Within <see cref="IRefreshTokenRotationPolicy.ConcurrentRotationGraceWindow"/>
    /// of that rotation, this is treated as a benign race (double submit,
    /// network retry, client timeout), never touching the Session.
    /// Afterwards, it is reuse of a token that should no longer exist on the
    /// client at all — the whole rotation chain is invalidated by revoking
    /// the Session that owns it (<see cref="Session"/>'s own contract), not
    /// by hunting down and individually revoking every token in the chain.
    /// </summary>
    private async Task<Result<AuthTokensResult>> HandleRotatedTokenPresentedAgainAsync(
        RefreshToken token, Guid tenantId, DateTimeOffset now, Guid correlationId, string? ipAddress,
        CancellationToken cancellationToken)
    {
        var elapsedSinceRotation = now - token.RevokedAt!.Value;
        if (elapsedSinceRotation <= _rotationPolicy.ConcurrentRotationGraceWindow)
        {
            return Reject(
                SecurityAuditEventType.RefreshRejected, SecurityAuditReasonCode.ConcurrentRotationGraceWindow,
                tenantId, now, correlationId, ipAddress, token.UserId, token.SessionId, token.TokenId);
        }

        var reuseDetected = new RefreshTokenReuseDetected
        {
            TenantId = tenantId,
            AggregateId = token.UserId,
            AggregateType = "User",
            CorrelationId = correlationId,
            ActorType = "User",
            ActorId = token.UserId.ToString(),
            SessionId = token.SessionId,
            TokenId = token.TokenId,
        };
        _eventCollector.Enqueue(reuseDetected);

        var session = await _sessionRepository.GetByIdAsync(token.SessionId, cancellationToken);
        if (session is not null)
        {
            session.Revoke(ReuseDetectedSessionRevocationReason, now);

            // Staged for the executor to write to ISessionRevocationCache
            // AFTER this transaction actually commits — never from inside it
            // (Incremento 2 plan, Etapa 12; see ISessionRevocationSignal).
            _revocationSignal.MarkRevoked(tenantId, session.Id);

            // Only when the session actually exists to be revoked — unlike
            // RefreshTokenReuseDetected above, which is published regardless
            // (reuse was genuinely detected either way), SessionRevoked
            // represents an actual revocation that happened (Documento 07
            // §13.1).
            _eventCollector.Enqueue(new SessionRevoked
            {
                TenantId = tenantId,
                AggregateId = token.UserId,
                AggregateType = "User",
                CorrelationId = correlationId,
                CausationId = reuseDetected.EventId,
                ActorType = "User",
                ActorId = token.UserId.ToString(),
                SessionId = session.Id,
                ReasonCode = SessionRevokedReasonCodes.RefreshTokenReuseDetected,
            });
        }

        return Reject(
            SecurityAuditEventType.RefreshTokenReuseDetected, reasonCode: null,
            tenantId, now, correlationId, ipAddress, token.UserId, token.SessionId, token.TokenId);
    }

    private Result<AuthTokensResult> Reject(
        SecurityAuditEventType eventType,
        SecurityAuditReasonCode? reasonCode,
        Guid tenantId,
        DateTimeOffset now,
        Guid correlationId,
        string? ipAddress,
        Guid? userId,
        Guid? sessionId,
        Guid? refreshTokenId)
    {
        RecordAudit(tenantId, now, correlationId, eventType, reasonCode, userId, ipAddress, sessionId, refreshTokenId);
        return Failure();
    }

    private static Result<AuthTokensResult> Failure() => Result.Failure<AuthTokensResult>(InvalidRefreshTokenError);

    private void RecordAudit(
        Guid tenantId,
        DateTimeOffset now,
        Guid correlationId,
        SecurityAuditEventType eventType,
        SecurityAuditReasonCode? reasonCode,
        Guid? userId,
        string? ipAddress,
        Guid? sessionId = null,
        Guid? refreshTokenId = null)
    {
        var entry = SecurityAuditEntry.Record(
            Guid.NewGuid(), tenantId, eventType, now, correlationId,
            reasonCode: reasonCode, userId: userId, sessionId: sessionId, refreshTokenId: refreshTokenId,
            ipAddress: ipAddress);

        _auditWriter.Record(entry);
    }

    private static string? Truncate(string? value, int maxLength) =>
        string.IsNullOrEmpty(value) || value.Length <= maxLength ? value : value[..maxLength];
}
