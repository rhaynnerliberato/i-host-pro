using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.Identity.Application.Errors;
using IHostPro.Contexts.Identity.Application.Sessions;
using IHostPro.Contexts.Identity.Contracts;
using IHostPro.Contexts.Identity.Domain;
using IHostPro.Contexts.Identity.Domain.Enums;

namespace IHostPro.Contexts.Identity.Application.Users;

/// <summary>
/// Resets a target user's password on an Administrator's behalf and forces
/// re-authentication on every session active at the time (Incremento 3,
/// Checkpoint 9). Runs inside the transaction <see cref="IAdminResetPasswordExecutor"/>
/// opens for this command — never calls <c>SaveChangesAsync</c> itself.
///
/// The self-reset guard is checked before touching the repository (Section 4:
/// "impedir que o Administrador use este endpoint contra si próprio") — a
/// cheap comparison of two already-known ids, ahead of any lookup or password
/// hashing. "Differs from current" is checked the same way
/// <see cref="ChangeOwnPasswordCommandHandler"/> does: verifying
/// <see cref="AdminResetPasswordCommand.NewPassword"/> against the target's
/// CURRENT hash via <see cref="IUserAuthenticationService.CheckPasswordAsync"/>
/// — hashes are salted, so they can never be compared directly. Resetting a
/// Blocked user's password never changes <see cref="UserStatus"/> — nothing
/// here touches it, so the user remains blocked until an explicit unblock.
///
/// <see cref="IUserSessionRevoker"/> is reused unchanged from Checkpoint 6/7,
/// exactly like <see cref="ChangeOwnPasswordCommandHandler"/>.
/// </summary>
public sealed class AdminResetPasswordCommandHandler : ICommandHandler<AdminResetPasswordCommand>
{
    private static readonly Error UserNotFoundError = new(IdentityErrorCodes.UserNotFound, IdentityErrorCodes.UserNotFound);
    private static readonly Error AdminCannotResetOwnPasswordError = new(
        IdentityErrorCodes.AdminCannotResetOwnPassword, IdentityErrorCodes.AdminCannotResetOwnPassword);
    private static readonly Error NewPasswordMustDifferError = new(
        IdentityErrorCodes.NewPasswordMustDiffer, IdentityErrorCodes.NewPasswordMustDiffer);

    private readonly IRepository<User, Guid> _userRepository;
    private readonly IUserAuthenticationService _userAuthenticationService;
    private readonly IUserProvisioningService _provisioningService;
    private readonly IUserSessionRevoker _sessionRevoker;
    private readonly ISecurityAuditWriter _auditWriter;
    private readonly IIntegrationEventCollector _eventCollector;
    private readonly TimeProvider _timeProvider;

    public AdminResetPasswordCommandHandler(
        IRepository<User, Guid> userRepository,
        IUserAuthenticationService userAuthenticationService,
        IUserProvisioningService provisioningService,
        IUserSessionRevoker sessionRevoker,
        ISecurityAuditWriter auditWriter,
        IIntegrationEventCollector eventCollector,
        TimeProvider timeProvider)
    {
        _userRepository = userRepository;
        _userAuthenticationService = userAuthenticationService;
        _provisioningService = provisioningService;
        _sessionRevoker = sessionRevoker;
        _auditWriter = auditWriter;
        _eventCollector = eventCollector;
        _timeProvider = timeProvider;
    }

    public async ValueTask<Result> Handle(AdminResetPasswordCommand command, CancellationToken cancellationToken)
    {
        var targetUser = await _userRepository.GetByIdAsync(command.TargetUserId, cancellationToken);
        if (targetUser is null)
            return Result.Failure(UserNotFoundError);

        if (command.TargetUserId == command.ActorId)
            return Result.Failure(AdminCannotResetOwnPasswordError);

        var passwordCheck = await _provisioningService.ValidatePasswordAsync(command.NewPassword);
        if (!passwordCheck.Succeeded)
        {
            return Result.Failure(
                new Error(string.Join(",", passwordCheck.ErrorCodes), "password_policy_violation"));
        }

        if (await _userAuthenticationService.CheckPasswordAsync(targetUser, command.NewPassword))
            return Result.Failure(NewPasswordMustDifferError);

        var now = _timeProvider.GetUtcNow();
        var newHash = _provisioningService.HashPassword(command.NewPassword);
        targetUser.SetPasswordHash(newHash, now);

        var correlationId = Guid.NewGuid();

        _auditWriter.Record(SecurityAuditEntry.Record(
            Guid.NewGuid(), command.TenantId, SecurityAuditEventType.PasswordResetByAdmin, now, correlationId,
            reasonCode: null, userId: command.TargetUserId, sessionId: null, refreshTokenId: null, ipAddress: null));

        var passwordChanged = new PasswordChanged
        {
            TenantId = command.TenantId,
            AggregateId = command.TargetUserId,
            AggregateType = "User",
            CorrelationId = correlationId,
            ActorType = "User",
            ActorId = command.ActorId.ToString(),
            ChangeType = PasswordChangeTypeCodes.AdminReset,
        };
        _eventCollector.Enqueue(passwordChanged);

        var revokedSessionIds = await _sessionRevoker.RevokeAllActiveSessionsAsync(
            command.TenantId, command.TargetUserId, SessionRevokedReasonCodes.PasswordChanged,
            RefreshTokenRevocationReason.PasswordChanged, now, cancellationToken);

        foreach (var sessionId in revokedSessionIds)
        {
            _eventCollector.Enqueue(new SessionRevoked
            {
                TenantId = command.TenantId,
                AggregateId = command.TargetUserId,
                AggregateType = "User",
                CorrelationId = correlationId,
                CausationId = passwordChanged.EventId,
                ActorType = "User",
                ActorId = command.ActorId.ToString(),
                SessionId = sessionId,
                ReasonCode = SessionRevokedReasonCodes.PasswordChanged,
            });
        }

        return Result.Success();
    }
}
