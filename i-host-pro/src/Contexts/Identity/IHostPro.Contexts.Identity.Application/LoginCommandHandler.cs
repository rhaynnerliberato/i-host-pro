using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.Identity.Contracts;
using IHostPro.Contexts.Identity.Domain;
using IHostPro.Contexts.Identity.Domain.Enums;

namespace IHostPro.Contexts.Identity.Application;

/// <summary>
/// Authenticates a user by e-mail/password within an already-resolved tenant
/// (Incremento 2 plan, Etapa 9). Runs entirely inside the transaction opened
/// by <c>TenantBootstrapBehavior</c> — never calls <c>SaveChangesAsync</c>
/// itself; every mutation below is staged only (via <c>UserManager</c>,
/// domain methods on an already-tracked entity, or a repository's
/// <c>Add</c>), persisted atomically once by
/// <c>ITenantAwareUnitOfWork.ExecuteAsync</c> at the end of the pipeline.
///
/// Every rejection path — tenant unresolved (handled in
/// <see cref="LoginTenantBootstrapResolver"/>), user not found, user
/// blocked, account locked out, wrong password — returns the exact same
/// <see cref="Error"/> (<see cref="InvalidCredentialsError"/>): the specific
/// reason is recorded only internally, in <c>security_audit_log</c>, never
/// surfaced to the caller (Incremento 2 plan, "resposta externa genérica").
///
/// Never logs, and never includes in any <see cref="Error"/>,
/// <see cref="LoginCommand.Password"/>, an access token, a refresh token, or
/// a token hash.
/// </summary>
public sealed class LoginCommandHandler : ICommandHandler<LoginCommand, AuthTokensResult>
{
    private static readonly Error InvalidCredentialsError = new("login_invalid_credentials", "login_invalid_credentials");

    private const int MaxDeviceLength = 200; // matches SessionConfiguration (Incremento 1)
    private const int MaxBrowserLength = 200;
    private const int MaxIpAddressLength = 45;

    private readonly IUserAuthenticationService _userManager;
    private readonly IUserRoleReader _roleReader;
    private readonly IRepository<Session, Guid> _sessionRepository;
    private readonly IRepository<RefreshToken, Guid> _refreshTokenRepository;
    private readonly IRefreshTokenGenerator _refreshTokenGenerator;
    private readonly IJwtTokenGenerator _jwtTokenGenerator;
    private readonly ISecurityAuditWriter _auditWriter;
    private readonly IIntegrationEventCollector _eventCollector;
    private readonly IDummyPasswordVerifier _dummyPasswordVerifier;
    private readonly ICurrentTenantProvider _currentTenantProvider;
    private readonly TimeProvider _timeProvider;

    public LoginCommandHandler(
        IUserAuthenticationService userManager,
        IUserRoleReader roleReader,
        IRepository<Session, Guid> sessionRepository,
        IRepository<RefreshToken, Guid> refreshTokenRepository,
        IRefreshTokenGenerator refreshTokenGenerator,
        IJwtTokenGenerator jwtTokenGenerator,
        ISecurityAuditWriter auditWriter,
        IIntegrationEventCollector eventCollector,
        IDummyPasswordVerifier dummyPasswordVerifier,
        ICurrentTenantProvider currentTenantProvider,
        TimeProvider timeProvider)
    {
        _userManager = userManager;
        _roleReader = roleReader;
        _sessionRepository = sessionRepository;
        _refreshTokenRepository = refreshTokenRepository;
        _refreshTokenGenerator = refreshTokenGenerator;
        _jwtTokenGenerator = jwtTokenGenerator;
        _auditWriter = auditWriter;
        _eventCollector = eventCollector;
        _dummyPasswordVerifier = dummyPasswordVerifier;
        _currentTenantProvider = currentTenantProvider;
        _timeProvider = timeProvider;
    }

    public async ValueTask<Result<AuthTokensResult>> Handle(LoginCommand command, CancellationToken cancellationToken)
    {
        var tenantId = _currentTenantProvider.TenantId;
        var now = _timeProvider.GetUtcNow();
        var correlationId = Guid.NewGuid();
        var ipAddress = Truncate(command.RequestContext.IpAddress, MaxIpAddressLength);

        var user = await _userManager.FindByEmailAsync(command.Email);

        if (user is null)
        {
            _dummyPasswordVerifier.Verify(command.Password);
            RecordAudit(tenantId, now, correlationId, SecurityAuditEventType.LoginRejected,
                SecurityAuditReasonCode.UserNotFound, userId: null, ipAddress);
            EnqueueLoginFailed(tenantId, correlationId, userId: null, LoginFailedReasonCodes.UserNotFound);
            return Failure();
        }

        if (user.Status == UserStatus.Blocked)
        {
            _dummyPasswordVerifier.Verify(command.Password);
            RecordAudit(tenantId, now, correlationId, SecurityAuditEventType.LoginRejected,
                SecurityAuditReasonCode.UserBlocked, user.Id, ipAddress);
            EnqueueLoginFailed(tenantId, correlationId, user.Id, LoginFailedReasonCodes.UserBlocked);
            return Failure();
        }

        if (await _userManager.IsLockedOutAsync(user))
        {
            _dummyPasswordVerifier.Verify(command.Password);
            RecordAudit(tenantId, now, correlationId, SecurityAuditEventType.LoginRejected,
                SecurityAuditReasonCode.AccountLocked, user.Id, ipAddress);
            EnqueueLoginFailed(tenantId, correlationId, user.Id, LoginFailedReasonCodes.AccountLocked);
            return Failure();
        }

        // CheckPasswordAsync (not PasswordSignInAsync — no SignInManager is
        // registered, only UserManager<User>, wrapped behind
        // IUserAuthenticationService so this Application-layer class never
        // references Microsoft.AspNetCore.Identity directly, Incremento 1
        // plan) does not itself touch FailedAccessCount/Lockout;
        // AccessFailedAsync below is the ASP.NET Core Identity method that
        // does, consulting IdentityOptions.Lockout (Etapa 9's
        // AccountLockoutOptions) — no threshold/duration is read or compared
        // directly by this handler.
        if (!await _userManager.CheckPasswordAsync(user, command.Password))
        {
            await _userManager.AccessFailedAsync(user);

            var loginFailed = EnqueueLoginFailed(tenantId, correlationId, user.Id, LoginFailedReasonCodes.InvalidPassword);

            if (await _userManager.IsLockedOutAsync(user))
            {
                RecordAudit(tenantId, now, correlationId, SecurityAuditEventType.AccountLockedOut,
                    reasonCode: null, user.Id, ipAddress);

                // Same operation that just triggered the lockout — the
                // accompanying LoginFailed above is this event's cause
                // (Documento 07 §13.1).
                _eventCollector.Enqueue(new AccountLockedOut
                {
                    TenantId = tenantId,
                    AggregateId = user.Id,
                    AggregateType = "User",
                    CorrelationId = correlationId,
                    CausationId = loginFailed.EventId,
                    ActorType = "User",
                    ActorId = user.Id.ToString(),
                    LockoutEnd = user.LockoutEnd!.Value,
                    ReasonCode = AccountLockedOutReasonCodes.MaxFailedAttempts,
                });
            }
            else
            {
                RecordAudit(tenantId, now, correlationId, SecurityAuditEventType.LoginRejected,
                    SecurityAuditReasonCode.InvalidPassword, user.Id, ipAddress);
            }

            return Failure();
        }

        await _userManager.ResetAccessFailedCountAsync(user);
        user.RecordSuccessfulLogin(now);

        var session = Session.Open(
            Guid.NewGuid(), tenantId, user.Id, now,
            device: Truncate(command.RequestContext.Device, MaxDeviceLength),
            browser: Truncate(command.RequestContext.Browser, MaxBrowserLength),
            ipAddress: ipAddress);
        _sessionRepository.Add(session);

        var generatedRefreshToken = _refreshTokenGenerator.Generate(tenantId);
        var refreshToken = RefreshToken.Issue(
            Guid.NewGuid(), generatedRefreshToken.TokenId, tenantId, session.Id, user.Id,
            generatedRefreshToken.TokenHash, now, generatedRefreshToken.ExpiresAt);
        _refreshTokenRepository.Add(refreshToken);

        var roles = await _roleReader.GetRoleCodesAsync(user.Id, cancellationToken);
        var accessToken = _jwtTokenGenerator.GenerateAccessToken(
            new JwtAccessTokenRequest(user.Id, tenantId, session.Id, roles));

        RecordAudit(tenantId, now, correlationId, SecurityAuditEventType.LoginSucceeded,
            reasonCode: null, user.Id, ipAddress, session.Id, generatedRefreshToken.TokenId);

        _eventCollector.Enqueue(new UserLoggedIn
        {
            TenantId = tenantId,
            AggregateId = user.Id,
            AggregateType = "User",
            CorrelationId = correlationId,
            ActorType = "User",
            ActorId = user.Id.ToString(),
            SessionId = session.Id,
        });

        var result = new AuthTokensResult(
            accessToken.Token, accessToken.ExpiresAt, generatedRefreshToken.Token, generatedRefreshToken.ExpiresAt);
        return Result.Success(result);
    }

    private static Result<AuthTokensResult> Failure() => Result.Failure<AuthTokensResult>(InvalidCredentialsError);

    private LoginFailed EnqueueLoginFailed(Guid tenantId, Guid correlationId, Guid? userId, string reasonCode)
    {
        // AggregateId/AggregateType are the tenant here, not the user:
        // userId can be null (no user exists for the attempted e-mail),
        // while the tenant is always known by the time this handler runs
        // (Documento 07 §13.1).
        var loginFailed = new LoginFailed
        {
            TenantId = tenantId,
            AggregateId = tenantId,
            AggregateType = "Tenant",
            CorrelationId = correlationId,
            ActorType = "User",
            ActorId = userId?.ToString(),
            UserId = userId,
            ReasonCode = reasonCode,
        };
        _eventCollector.Enqueue(loginFailed);
        return loginFailed;
    }

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
