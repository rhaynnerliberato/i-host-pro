using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.Identity.Application.Errors;
using IHostPro.Contexts.Identity.Application.Sessions;
using IHostPro.Contexts.Identity.Contracts;
using IHostPro.Contexts.Identity.Domain;
using IHostPro.Contexts.Identity.Domain.Enums;

namespace IHostPro.Contexts.Identity.Application.Users;

/// <summary>
/// Changes the authenticated caller's own password and forces re-authentication
/// on every session active at the time, including the one that originated this
/// request (Incremento 3, Checkpoint 9). Runs inside the transaction
/// <see cref="IChangeOwnPasswordExecutor"/> opens for this command — never
/// calls <c>SaveChangesAsync</c> itself.
///
/// Validation order matches the approved decision (Section 3) exactly: current
/// password verified, then new password checked against the existing policy,
/// then confirmed to differ from the current one — the new hash is computed
/// only once every earlier check has passed ("não executar hash da nova senha
/// antes de validar a senha atual e a política"). "Differs from current" is
/// checked by verifying <see cref="ChangeOwnPasswordCommand.NewPassword"/>
/// against the user's CURRENT hash via the same
/// <see cref="IUserAuthenticationService.CheckPasswordAsync"/> abstraction
/// Login already uses — hashes are salted, so they can never be compared
/// directly.
///
/// <see cref="IUserSessionRevoker"/> is reused unchanged from Checkpoint 6/7 —
/// the same cascade Block/AssignRole/RemoveRole already use, generalized to
/// every active session of a user (Section 7 of the Checkpoint 6 decision:
/// "o componente deve ser reutilizável").
/// </summary>
public sealed class ChangeOwnPasswordCommandHandler : ICommandHandler<ChangeOwnPasswordCommand>
{
    private static readonly Error AuthenticatedUserNotFoundError = new(
        IdentityErrorCodes.AuthenticatedUserNotFound, IdentityErrorCodes.AuthenticatedUserNotFound);
    private static readonly Error InvalidCurrentPasswordError = new(
        IdentityErrorCodes.InvalidCurrentPassword, IdentityErrorCodes.InvalidCurrentPassword);
    private static readonly Error NewPasswordMustDifferError = new(
        IdentityErrorCodes.NewPasswordMustDiffer, IdentityErrorCodes.NewPasswordMustDiffer);

    private readonly IRepository<User, Guid> _userRepository;
    private readonly IUserAuthenticationService _userAuthenticationService;
    private readonly IUserProvisioningService _provisioningService;
    private readonly IUserSessionRevoker _sessionRevoker;
    private readonly ISecurityAuditWriter _auditWriter;
    private readonly IIntegrationEventCollector _eventCollector;
    private readonly TimeProvider _timeProvider;

    public ChangeOwnPasswordCommandHandler(
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

    public async ValueTask<Result> Handle(ChangeOwnPasswordCommand command, CancellationToken cancellationToken)
    {
        var user = await _userRepository.GetByIdAsync(command.UserId, cancellationToken);

        // Folds "the authenticated user's row no longer exists" (Checkpoint
        // 4's defensive case) and "exists but is not Active" into the same
        // outcome and the same error code — approved in the Checkpoint 9
        // follow-up review — precisely so this endpoint never reveals a
        // blocked account's internal state through a distinct response: a
        // caller whose own account is unusable for this action gets the exact
        // same 404 regardless of the specific reason, mirroring how
        // RevokeOwnSession folds "nonexistent/foreign/inactive" into one
        // outcome for its own session-ownership check.
        if (user is null || user.Status != UserStatus.Active)
            return Result.Failure(AuthenticatedUserNotFoundError);

        if (!await _userAuthenticationService.CheckPasswordAsync(user, command.CurrentPassword))
            return Result.Failure(InvalidCurrentPasswordError);

        var passwordCheck = await _provisioningService.ValidatePasswordAsync(command.NewPassword);
        if (!passwordCheck.Succeeded)
        {
            return Result.Failure(
                new Error(string.Join(",", passwordCheck.ErrorCodes), "password_policy_violation"));
        }

        if (await _userAuthenticationService.CheckPasswordAsync(user, command.NewPassword))
            return Result.Failure(NewPasswordMustDifferError);

        var now = _timeProvider.GetUtcNow();
        var newHash = _provisioningService.HashPassword(command.NewPassword);
        user.SetPasswordHash(newHash, now);

        var correlationId = Guid.NewGuid();

        _auditWriter.Record(SecurityAuditEntry.Record(
            Guid.NewGuid(), command.TenantId, SecurityAuditEventType.PasswordChangedBySelf, now, correlationId,
            reasonCode: null, userId: command.UserId, sessionId: null, refreshTokenId: null, ipAddress: null));

        var passwordChanged = new PasswordChanged
        {
            TenantId = command.TenantId,
            AggregateId = command.UserId,
            AggregateType = "User",
            CorrelationId = correlationId,
            ActorType = "User",
            ActorId = command.UserId.ToString(),
            ChangeType = PasswordChangeTypeCodes.Self,
        };
        _eventCollector.Enqueue(passwordChanged);

        var revokedSessionIds = await _sessionRevoker.RevokeAllActiveSessionsAsync(
            command.TenantId, command.UserId, SessionRevokedReasonCodes.PasswordChanged,
            RefreshTokenRevocationReason.PasswordChanged, now, cancellationToken);

        foreach (var sessionId in revokedSessionIds)
        {
            _eventCollector.Enqueue(new SessionRevoked
            {
                TenantId = command.TenantId,
                AggregateId = command.UserId,
                AggregateType = "User",
                CorrelationId = correlationId,
                CausationId = passwordChanged.EventId,
                ActorType = "User",
                ActorId = command.UserId.ToString(),
                SessionId = sessionId,
                ReasonCode = SessionRevokedReasonCodes.PasswordChanged,
            });
        }

        return Result.Success();
    }
}
