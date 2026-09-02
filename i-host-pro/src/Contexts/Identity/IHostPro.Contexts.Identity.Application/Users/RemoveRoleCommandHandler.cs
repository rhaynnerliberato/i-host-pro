using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.Identity.Application.Catalog;
using IHostPro.Contexts.Identity.Application.Errors;
using IHostPro.Contexts.Identity.Application.Sessions;
using IHostPro.Contexts.Identity.Contracts;
using IHostPro.Contexts.Identity.Domain;
using IHostPro.Contexts.Identity.Domain.Enums;

namespace IHostPro.Contexts.Identity.Application.Users;

/// <summary>
/// Removes a platform role from a user and forces re-authentication on every
/// session that was active at the time (Incremento 3, Checkpoint 6). Runs
/// inside the transaction <see cref="IRemoveRoleExecutor"/> opens for this
/// command — never calls <c>SaveChangesAsync</c> itself.
///
/// Two business-rule rejections guard against ever leaving a user or a
/// tenant without administration: a user's own last role
/// (<see cref="IdentityErrorCodes.UserMustHaveAtLeastOneRole"/>, checked
/// in-memory against the role list already fetched — a role swap must assign
/// the new role first, then remove the old one, per Section 4) and, only
/// when the role being removed is <c>ADMIN</c>, the tenant's last active
/// Administrator (<see cref="ILastAdministratorGuard"/>, Section 5). Neither
/// check nor the removal itself runs if the role was never assigned in the
/// first place (<see cref="IdentityErrorCodes.RoleNotAssigned"/>) — that path
/// performs no side effect at all (Section 3).
/// </summary>
public sealed class RemoveRoleCommandHandler : ICommandHandler<RemoveRoleCommand>
{
    private const string AdministratorRoleCode = "ADMIN";

    private static readonly Error UserNotFoundError = new(IdentityErrorCodes.UserNotFound, IdentityErrorCodes.UserNotFound);
    private static readonly Error RoleNotFoundError = new(IdentityErrorCodes.RoleNotFound, IdentityErrorCodes.RoleNotFound);
    private static readonly Error RoleNotAssignedError = new(IdentityErrorCodes.RoleNotAssigned, IdentityErrorCodes.RoleNotAssigned);
    private static readonly Error UserMustHaveAtLeastOneRoleError = new(
        IdentityErrorCodes.UserMustHaveAtLeastOneRole, IdentityErrorCodes.UserMustHaveAtLeastOneRole);
    private static readonly Error LastActiveAdministratorError = new(
        IdentityErrorCodes.LastActiveAdministrator, IdentityErrorCodes.LastActiveAdministrator);

    private readonly IRepository<User, Guid> _userRepository;
    private readonly IIdentityCatalogReader _catalogReader;
    private readonly IUserRoleReader _userRoleReader;
    private readonly IUserRoleWriter _userRoleWriter;
    private readonly ILastAdministratorGuard _lastAdministratorGuard;
    private readonly IUserSessionRevoker _sessionRevoker;
    private readonly ISecurityAuditWriter _auditWriter;
    private readonly IIntegrationEventCollector _eventCollector;
    private readonly TimeProvider _timeProvider;

    public RemoveRoleCommandHandler(
        IRepository<User, Guid> userRepository,
        IIdentityCatalogReader catalogReader,
        IUserRoleReader userRoleReader,
        IUserRoleWriter userRoleWriter,
        ILastAdministratorGuard lastAdministratorGuard,
        IUserSessionRevoker sessionRevoker,
        ISecurityAuditWriter auditWriter,
        IIntegrationEventCollector eventCollector,
        TimeProvider timeProvider)
    {
        _userRepository = userRepository;
        _catalogReader = catalogReader;
        _userRoleReader = userRoleReader;
        _userRoleWriter = userRoleWriter;
        _lastAdministratorGuard = lastAdministratorGuard;
        _sessionRevoker = sessionRevoker;
        _auditWriter = auditWriter;
        _eventCollector = eventCollector;
        _timeProvider = timeProvider;
    }

    public async ValueTask<Result> Handle(RemoveRoleCommand command, CancellationToken cancellationToken)
    {
        var targetUser = await _userRepository.GetByIdAsync(command.TargetUserId, cancellationToken);
        if (targetUser is null)
            return Result.Failure(UserNotFoundError);

        var roles = await _catalogReader.ListRolesAsync(cancellationToken);
        if (!roles.Any(r => string.Equals(r.Code, command.RoleCode, StringComparison.Ordinal)))
            return Result.Failure(RoleNotFoundError);

        var currentRoleCodes = await _userRoleReader.GetRoleCodesAsync(command.TargetUserId, cancellationToken);
        if (!currentRoleCodes.Contains(command.RoleCode, StringComparer.Ordinal))
            return Result.Failure(RoleNotAssignedError);

        // In-memory against the list already fetched above — never a fresh
        // COUNT query after staging the removal below, which would still see
        // the pre-removal row (SaveChangesAsync has not run yet).
        if (currentRoleCodes.Count == 1)
            return Result.Failure(UserMustHaveAtLeastOneRoleError);

        if (string.Equals(command.RoleCode, AdministratorRoleCode, StringComparison.Ordinal))
        {
            var anotherActiveAdministratorRemains = await _lastAdministratorGuard.AnotherActiveAdministratorRemainsAsync(
                command.TenantId, command.TargetUserId, cancellationToken);
            if (!anotherActiveAdministratorRemains)
                return Result.Failure(LastActiveAdministratorError);
        }

        // Guaranteed non-null: currentRoleCodes (read moments ago, same
        // transaction, single request) already confirmed this exact
        // (userId, roleCode) pair is assigned.
        var userRole = await _userRoleReader.FindAsync(command.TargetUserId, command.RoleCode, cancellationToken);
        _userRoleWriter.Remove(userRole!);

        var now = _timeProvider.GetUtcNow();
        var correlationId = Guid.NewGuid();

        _auditWriter.Record(SecurityAuditEntry.Record(
            Guid.NewGuid(), command.TenantId, SecurityAuditEventType.UserRoleRemoved, now, correlationId,
            reasonCode: null, userId: command.TargetUserId, actorId: command.ActorId, sessionId: null, refreshTokenId: null, ipAddress: null));

        var roleRemoved = new UserRoleRemoved
        {
            TenantId = command.TenantId,
            AggregateId = command.TargetUserId,
            AggregateType = "User",
            CorrelationId = correlationId,
            ActorType = "User",
            ActorId = command.ActorId.ToString(),
            RoleCode = command.RoleCode,
        };
        _eventCollector.Enqueue(roleRemoved);

        var revokedSessionIds = await _sessionRevoker.RevokeAllActiveSessionsAsync(
            command.TenantId, command.TargetUserId, SessionRevokedReasonCodes.RolesChanged,
            RefreshTokenRevocationReason.RolesChanged, now, cancellationToken);

        foreach (var sessionId in revokedSessionIds)
        {
            _eventCollector.Enqueue(new SessionRevoked
            {
                TenantId = command.TenantId,
                AggregateId = command.TargetUserId,
                AggregateType = "User",
                CorrelationId = correlationId,
                CausationId = roleRemoved.EventId,
                ActorType = "User",
                ActorId = command.ActorId.ToString(),
                SessionId = sessionId,
                ReasonCode = SessionRevokedReasonCodes.RolesChanged,
            });
        }

        return Result.Success();
    }
}
