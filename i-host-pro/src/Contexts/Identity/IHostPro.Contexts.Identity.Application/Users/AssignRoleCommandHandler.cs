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
/// Assigns a platform role to a user and forces re-authentication on every
/// session that was active at the time (Incremento 3, Checkpoint 6). Runs
/// inside the transaction <see cref="IAssignRoleExecutor"/> opens for this
/// command — never calls <c>SaveChangesAsync</c> itself; every mutation
/// below is staged only.
///
/// A role already held is idempotent, not an error state worth silently
/// succeeding on: rejected with <see cref="IdentityErrorCodes.RoleAlreadyAssigned"/>,
/// no state changed, no audit, no event, no session revoked (Section 3 —
/// "operações repetidas não podem revogar sessões... emitir eventos...").
/// </summary>
public sealed class AssignRoleCommandHandler : ICommandHandler<AssignRoleCommand>
{
    private static readonly Error UserNotFoundError = new(IdentityErrorCodes.UserNotFound, IdentityErrorCodes.UserNotFound);
    private static readonly Error RoleNotFoundError = new(IdentityErrorCodes.RoleNotFound, IdentityErrorCodes.RoleNotFound);
    private static readonly Error RoleAlreadyAssignedError = new(IdentityErrorCodes.RoleAlreadyAssigned, IdentityErrorCodes.RoleAlreadyAssigned);

    private readonly IRepository<User, Guid> _userRepository;
    private readonly IIdentityCatalogReader _catalogReader;
    private readonly IUserRoleReader _userRoleReader;
    private readonly IUserRoleWriter _userRoleWriter;
    private readonly IUserSessionRevoker _sessionRevoker;
    private readonly ISecurityAuditWriter _auditWriter;
    private readonly IIntegrationEventCollector _eventCollector;
    private readonly TimeProvider _timeProvider;

    public AssignRoleCommandHandler(
        IRepository<User, Guid> userRepository,
        IIdentityCatalogReader catalogReader,
        IUserRoleReader userRoleReader,
        IUserRoleWriter userRoleWriter,
        IUserSessionRevoker sessionRevoker,
        ISecurityAuditWriter auditWriter,
        IIntegrationEventCollector eventCollector,
        TimeProvider timeProvider)
    {
        _userRepository = userRepository;
        _catalogReader = catalogReader;
        _userRoleReader = userRoleReader;
        _userRoleWriter = userRoleWriter;
        _sessionRevoker = sessionRevoker;
        _auditWriter = auditWriter;
        _eventCollector = eventCollector;
        _timeProvider = timeProvider;
    }

    public async ValueTask<Result> Handle(AssignRoleCommand command, CancellationToken cancellationToken)
    {
        // A user of another tenant is invisible by construction here (RLS +
        // Global Query Filter on IRepository<User,Guid>), so this branch also
        // covers "target belongs to a different tenant" — indistinguishable
        // from nonexistent, as required (Section 3).
        var targetUser = await _userRepository.GetByIdAsync(command.TargetUserId, cancellationToken);
        if (targetUser is null)
            return Result.Failure(UserNotFoundError);

        var roles = await _catalogReader.ListRolesAsync(cancellationToken);
        if (!roles.Any(r => string.Equals(r.Code, command.RoleCode, StringComparison.Ordinal)))
            return Result.Failure(RoleNotFoundError);

        var currentRoleCodes = await _userRoleReader.GetRoleCodesAsync(command.TargetUserId, cancellationToken);
        if (currentRoleCodes.Contains(command.RoleCode, StringComparer.Ordinal))
            return Result.Failure(RoleAlreadyAssignedError);

        var now = _timeProvider.GetUtcNow();

        var userRole = new UserRole(command.TenantId, command.TargetUserId, command.RoleCode, now, command.ActorId);
        _userRoleWriter.Assign(userRole);

        var correlationId = Guid.NewGuid();

        _auditWriter.Record(SecurityAuditEntry.Record(
            Guid.NewGuid(), command.TenantId, SecurityAuditEventType.UserRoleAssigned, now, correlationId,
            reasonCode: null, userId: command.TargetUserId, sessionId: null, refreshTokenId: null, ipAddress: null));

        var roleAssigned = new UserRoleAssigned
        {
            TenantId = command.TenantId,
            AggregateId = command.TargetUserId,
            AggregateType = "User",
            CorrelationId = correlationId,
            ActorType = "User",
            ActorId = command.ActorId.ToString(),
            RoleCode = command.RoleCode,
        };
        _eventCollector.Enqueue(roleAssigned);

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
                CausationId = roleAssigned.EventId,
                ActorType = "User",
                ActorId = command.ActorId.ToString(),
                SessionId = sessionId,
                ReasonCode = SessionRevokedReasonCodes.RolesChanged,
            });
        }

        return Result.Success();
    }
}
