using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.Identity.Application.Errors;
using IHostPro.Contexts.Identity.Contracts;
using IHostPro.Contexts.Identity.Domain;
using IHostPro.Contexts.Identity.Domain.Enums;

namespace IHostPro.Contexts.Identity.Application.Users;

/// <summary>
/// Unblocks a user (Incremento 3, Checkpoint 7). Runs inside the transaction
/// the shared <c>IIdentityTransactionExecutor</c> opens for this command —
/// never calls <c>SaveChangesAsync</c> itself.
///
/// Deliberately narrower than <see cref="BlockUserCommandHandler"/>: no
/// session/refresh-token cascade, no <see cref="ILastAdministratorGuard"/>
/// consultation (unblocking can never reduce the tenant's active
/// Administrator count), no <c>ISessionRevocationSignal</c>/Redis
/// involvement at all — a session revoked before the block stays revoked;
/// unblocking never restores or recreates one (Section 6). This command also
/// needs no bounded concurrency retry (Section 7): the only row it mutates is
/// the target <c>User</c> itself, and the one command that could otherwise
/// race on that exact row — <c>LoginCommandHandler</c> — never mutates a
/// blocked user's row at all on its rejection path (it stops at the
/// <c>Status == Blocked</c> check, before any <c>AccessFailedAsync</c>/
/// <c>RecordSuccessfulLogin</c> call), so no genuine
/// <c>DbUpdateConcurrencyException</c> is possible here.
/// </summary>
public sealed class UnblockUserCommandHandler : ICommandHandler<UnblockUserCommand>
{
    private static readonly Error UserNotFoundError = new(IdentityErrorCodes.UserNotFound, IdentityErrorCodes.UserNotFound);
    private static readonly Error UserAlreadyActiveError = new(IdentityErrorCodes.UserAlreadyActive, IdentityErrorCodes.UserAlreadyActive);

    private readonly IRepository<User, Guid> _userRepository;
    private readonly ISecurityAuditWriter _auditWriter;
    private readonly IIntegrationEventCollector _eventCollector;
    private readonly TimeProvider _timeProvider;

    public UnblockUserCommandHandler(
        IRepository<User, Guid> userRepository,
        ISecurityAuditWriter auditWriter,
        IIntegrationEventCollector eventCollector,
        TimeProvider timeProvider)
    {
        _userRepository = userRepository;
        _auditWriter = auditWriter;
        _eventCollector = eventCollector;
        _timeProvider = timeProvider;
    }

    public async ValueTask<Result> Handle(UnblockUserCommand command, CancellationToken cancellationToken)
    {
        var targetUser = await _userRepository.GetByIdAsync(command.TargetUserId, cancellationToken);
        if (targetUser is null)
            return Result.Failure(UserNotFoundError);

        if (targetUser.Status != UserStatus.Blocked)
            return Result.Failure(UserAlreadyActiveError);

        var now = _timeProvider.GetUtcNow();
        targetUser.Unblock(now);

        var correlationId = Guid.NewGuid();

        _auditWriter.Record(SecurityAuditEntry.Record(
            Guid.NewGuid(), command.TenantId, SecurityAuditEventType.UserUnblocked, now, correlationId,
            reasonCode: null, userId: command.TargetUserId, sessionId: null, refreshTokenId: null, ipAddress: null));

        _eventCollector.Enqueue(new UserUnblocked
        {
            TenantId = command.TenantId,
            AggregateId = command.TargetUserId,
            AggregateType = "User",
            CorrelationId = correlationId,
            ActorType = "User",
            ActorId = command.ActorId.ToString(),
        });

        return Result.Success();
    }
}
