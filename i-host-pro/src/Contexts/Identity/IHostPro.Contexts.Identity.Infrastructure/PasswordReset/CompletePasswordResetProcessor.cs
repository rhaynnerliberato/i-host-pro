using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.Contexts.Identity.Application;
using IHostPro.Contexts.Identity.Application.Errors;
using IHostPro.Contexts.Identity.Application.Sessions;
using IHostPro.Contexts.Identity.Contracts;
using IHostPro.Contexts.Identity.Domain;
using IHostPro.Contexts.Identity.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace IHostPro.Contexts.Identity.Infrastructure.PasswordReset;

/// <inheritdoc cref="ICompletePasswordResetProcessor"/>
/// <remarks>
/// The ONE class in Identity that calls <see cref="ITenantContext.SetTenant"/>
/// for the forgot-password flow's completion — only after
/// <see cref="IPasswordResetTokenRepository.ConsumeByTokenHashAsync"/> has
/// already returned a trusted result. Never trusts a tenant/user id from
/// anything the caller sent directly. Mirrors
/// <c>AirbnbEmailWebOAuthCallbackProcessor</c>'s exact bootstrap shape, and
/// <c>AdminResetPasswordExecutor</c>'s post-commit session-revocation-cache
/// ordering (drain <see cref="ISessionRevocationSignal"/> into
/// <see cref="ISessionRevocationCache"/> only after the transaction commits).
/// </remarks>
public sealed class CompletePasswordResetProcessor : ICompletePasswordResetProcessor
{
    private static readonly Error TokenInvalidError = new(IdentityErrorCodes.PasswordResetTokenInvalid, IdentityErrorCodes.PasswordResetTokenInvalid);

    private readonly IPasswordResetTokenRepository _tokenRepository;
    private readonly ITenantContext _tenantContext;
    private readonly IIdentityTransactionExecutor _transactionExecutor;
    private readonly IRepository<User, Guid> _userRepository;
    private readonly IUserProvisioningService _provisioningService;
    private readonly IUserSessionRevoker _sessionRevoker;
    private readonly ISessionRevocationSignal _revocationSignal;
    private readonly ISessionRevocationCache _revocationCache;
    private readonly ISecurityAuditWriter _auditWriter;
    private readonly IIntegrationEventCollector _eventCollector;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<CompletePasswordResetProcessor> _logger;

    public CompletePasswordResetProcessor(
        IPasswordResetTokenRepository tokenRepository,
        ITenantContext tenantContext,
        IIdentityTransactionExecutor transactionExecutor,
        IRepository<User, Guid> userRepository,
        IUserProvisioningService provisioningService,
        IUserSessionRevoker sessionRevoker,
        ISessionRevocationSignal revocationSignal,
        ISessionRevocationCache revocationCache,
        ISecurityAuditWriter auditWriter,
        IIntegrationEventCollector eventCollector,
        TimeProvider timeProvider,
        ILogger<CompletePasswordResetProcessor> logger)
    {
        _tokenRepository = tokenRepository;
        _tenantContext = tenantContext;
        _transactionExecutor = transactionExecutor;
        _userRepository = userRepository;
        _provisioningService = provisioningService;
        _sessionRevoker = sessionRevoker;
        _revocationSignal = revocationSignal;
        _revocationCache = revocationCache;
        _auditWriter = auditWriter;
        _eventCollector = eventCollector;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<Result> ProcessAsync(string token, string newPassword, CancellationToken cancellationToken)
    {
        // Stateless — never needs a resolved tenant/user — checked BEFORE
        // consuming the token, so a caller who fails policy can retry with
        // the same link instead of burning their one-time token.
        var passwordCheck = await _provisioningService.ValidatePasswordAsync(newPassword);
        if (!passwordCheck.Succeeded)
            return Result.Failure(new Error(string.Join(",", passwordCheck.ErrorCodes), "password_policy_violation"));

        var tokenHash = PasswordResetTokenHasher.Hash(token);
        var consumption = await _tokenRepository.ConsumeByTokenHashAsync(tokenHash, _timeProvider.GetUtcNow(), cancellationToken);
        if (consumption is null)
        {
            _logger.LogWarning("Forgot-password completion presented an invalid, expired, or already-consumed token.");
            return Result.Failure(TokenInvalidError);
        }

        // ONLY NOW is the tenant trusted - recovered exclusively from the
        // just-consumed token row, never from any request input.
        _tenantContext.SetTenant(consumption.TenantId);

        var result = await _transactionExecutor.ExecuteAsync(async () =>
        {
            var user = await _userRepository.GetByIdAsync(consumption.UserId, cancellationToken);
            if (user is null || user.Status != UserStatus.Active)
                return Result.Failure(TokenInvalidError);

            var now = _timeProvider.GetUtcNow();
            var newHash = _provisioningService.HashPassword(newPassword);
            user.SetPasswordHash(newHash, now);

            var correlationId = Guid.NewGuid();

            _auditWriter.Record(SecurityAuditEntry.Record(
                Guid.NewGuid(), consumption.TenantId, SecurityAuditEventType.PasswordResetCompleted, now, correlationId,
                reasonCode: null, userId: consumption.UserId, sessionId: null, refreshTokenId: null, ipAddress: null));

            var passwordChanged = new PasswordChanged
            {
                TenantId = consumption.TenantId,
                AggregateId = consumption.UserId,
                AggregateType = "User",
                CorrelationId = correlationId,
                ActorType = "User",
                ActorId = consumption.UserId.ToString(),
                ChangeType = PasswordChangeTypeCodes.SelfServiceReset,
            };
            _eventCollector.Enqueue(passwordChanged);

            var revokedSessionIds = await _sessionRevoker.RevokeAllActiveSessionsAsync(
                consumption.TenantId, consumption.UserId, SessionRevokedReasonCodes.PasswordChanged,
                RefreshTokenRevocationReason.PasswordChanged, now, cancellationToken);

            foreach (var sessionId in revokedSessionIds)
            {
                _eventCollector.Enqueue(new SessionRevoked
                {
                    TenantId = consumption.TenantId,
                    AggregateId = consumption.UserId,
                    AggregateType = "User",
                    CorrelationId = correlationId,
                    CausationId = passwordChanged.EventId,
                    ActorType = "User",
                    ActorId = consumption.UserId.ToString(),
                    SessionId = sessionId,
                    ReasonCode = SessionRevokedReasonCodes.PasswordChanged,
                });
            }

            return Result.Success();
        }, cancellationToken);

        foreach (var (tenantId, sessionId) in _revocationSignal.Drain())
            await _revocationCache.MarkRevokedAsync(tenantId, sessionId, cancellationToken);

        return result;
    }
}
