using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.Identity.Application.Errors;
using IHostPro.Contexts.Identity.Application.Sessions;
using IHostPro.Contexts.Identity.Contracts;
using IHostPro.Contexts.Identity.Domain;
using IHostPro.Contexts.Identity.Domain.Enums;

namespace IHostPro.Contexts.Identity.Application.Users;

/// <summary>
/// Blocks a user and forces re-authentication on every session that was
/// active at the time (Incremento 3, Checkpoint 7). Runs inside the
/// transaction <see cref="IBlockUserExecutor"/> opens for this command —
/// never calls <c>SaveChangesAsync</c> itself.
///
/// The last-Administrator guard (<see cref="ILastAdministratorGuard"/>,
/// reused unchanged from Checkpoint 6) is consulted only when the target
/// currently holds the <c>ADMIN</c> role — the SAME advisory lock key
/// (<c>ihostpro:identity:admin-guard:{tenantId}</c>) that
/// <c>RemoveRoleCommandHandler</c> uses, so a concurrent "remove ADMIN role"
/// and "block this Administrator" for two different last-Administrators of
/// the same tenant correctly serialize against each other, never both
/// succeeding (Section 4).
/// </summary>
public sealed class BlockUserCommandHandler : ICommandHandler<BlockUserCommand>
{
    private const string AdministratorRoleCode = "ADMIN";

    private static readonly Error UserNotFoundError = new(IdentityErrorCodes.UserNotFound, IdentityErrorCodes.UserNotFound);
    private static readonly Error UserAlreadyBlockedError = new(IdentityErrorCodes.UserAlreadyBlocked, IdentityErrorCodes.UserAlreadyBlocked);
    private static readonly Error LastActiveAdministratorError = new(
        IdentityErrorCodes.LastActiveAdministrator, IdentityErrorCodes.LastActiveAdministrator);

    private readonly IRepository<User, Guid> _userRepository;
    private readonly IUserRoleReader _userRoleReader;
    private readonly ILastAdministratorGuard _lastAdministratorGuard;
    private readonly IUserSessionRevoker _sessionRevoker;
    private readonly ISecurityAuditWriter _auditWriter;
    private readonly IIntegrationEventCollector _eventCollector;
    private readonly TimeProvider _timeProvider;

    public BlockUserCommandHandler(
        IRepository<User, Guid> userRepository,
        IUserRoleReader userRoleReader,
        ILastAdministratorGuard lastAdministratorGuard,
        IUserSessionRevoker sessionRevoker,
        ISecurityAuditWriter auditWriter,
        IIntegrationEventCollector eventCollector,
        TimeProvider timeProvider)
    {
        _userRepository = userRepository;
        _userRoleReader = userRoleReader;
        _lastAdministratorGuard = lastAdministratorGuard;
        _sessionRevoker = sessionRevoker;
        _auditWriter = auditWriter;
        _eventCollector = eventCollector;
        _timeProvider = timeProvider;
    }

    public async ValueTask<Result> Handle(BlockUserCommand command, CancellationToken cancellationToken)
    {
        var targetUser = await _userRepository.GetByIdAsync(command.TargetUserId, cancellationToken);
        if (targetUser is null)
            return Result.Failure(UserNotFoundError);

        if (targetUser.Status == UserStatus.Blocked)
            return Result.Failure(UserAlreadyBlockedError);

        var currentRoleCodes = await _userRoleReader.GetRoleCodesAsync(command.TargetUserId, cancellationToken);
        if (currentRoleCodes.Contains(AdministratorRoleCode, StringComparer.Ordinal))
        {
            var anotherActiveAdministratorRemains = await _lastAdministratorGuard.AnotherActiveAdministratorRemainsAsync(
                command.TenantId, command.TargetUserId, cancellationToken);
            if (!anotherActiveAdministratorRemains)
                return Result.Failure(LastActiveAdministratorError);
        }

        var now = _timeProvider.GetUtcNow();
        targetUser.Block(now);

        var correlationId = Guid.NewGuid();

        _auditWriter.Record(SecurityAuditEntry.Record(
            Guid.NewGuid(), command.TenantId, SecurityAuditEventType.UserBlocked, now, correlationId,
            reasonCode: null, userId: command.TargetUserId, actorId: command.ActorId, sessionId: null, refreshTokenId: null, ipAddress: null));

        var userBlocked = new UserBlocked
        {
            TenantId = command.TenantId,
            AggregateId = command.TargetUserId,
            AggregateType = "User",
            CorrelationId = correlationId,
            ActorType = "User",
            ActorId = command.ActorId.ToString(),
        };
        _eventCollector.Enqueue(userBlocked);

        var revokedSessionIds = await _sessionRevoker.RevokeAllActiveSessionsAsync(
            command.TenantId, command.TargetUserId, SessionRevokedReasonCodes.UserBlocked,
            RefreshTokenRevocationReason.UserBlocked, now, cancellationToken);

        foreach (var sessionId in revokedSessionIds)
        {
            _eventCollector.Enqueue(new SessionRevoked
            {
                TenantId = command.TenantId,
                AggregateId = command.TargetUserId,
                AggregateType = "User",
                CorrelationId = correlationId,
                CausationId = userBlocked.EventId,
                ActorType = "User",
                ActorId = command.ActorId.ToString(),
                SessionId = sessionId,
                ReasonCode = SessionRevokedReasonCodes.UserBlocked,
            });
        }

        return Result.Success();
    }
}
