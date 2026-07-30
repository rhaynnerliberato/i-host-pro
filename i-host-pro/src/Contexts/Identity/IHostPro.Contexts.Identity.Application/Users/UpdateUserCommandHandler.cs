using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.Identity.Application.Errors;
using IHostPro.Contexts.Identity.Contracts;
using IHostPro.Contexts.Identity.Domain;
using IHostPro.Contexts.Identity.Domain.Enums;
using IHostPro.Contexts.Identity.Domain.ValueObjects;

namespace IHostPro.Contexts.Identity.Application.Users;

/// <summary>
/// Updates a user's full name and/or e-mail, idempotently (Incremento 3,
/// Checkpoint 8). Runs inside the transaction <see cref="IUpdateUserExecutor"/>
/// opens for this command — never calls <c>SaveChangesAsync</c> itself.
///
/// Idempotency (Section 4): each supplied field is compared, after the SAME
/// normalization the domain itself applies (<c>FullName.Trim()</c>,
/// <c>Email.NormalizedValue</c>), against the user's CURRENT value. Only
/// fields that genuinely change are applied to the domain, recorded in
/// <see cref="UserUpdated.ChangedFields"/>, and cause an audit
/// entry/event to be emitted at all — a no-op request (every supplied field
/// already matches) still returns <c>200</c> with the current state, but
/// stages no mutation, so <see cref="IIdentityTransactionExecutor"/> commits
/// an empty transaction: no audit, no event, no Redis, no bumped
/// <c>UpdatedAt</c> (Section 4: "não modificar timestamps de atualização
/// artificialmente").
/// </summary>
public sealed class UpdateUserCommandHandler : ICommandHandler<UpdateUserCommand, UserResult>
{
    private static readonly Error UserNotFoundError = new(IdentityErrorCodes.UserNotFound, IdentityErrorCodes.UserNotFound);
    private static readonly Error NoChangesProvidedError = new(IdentityErrorCodes.NoChangesProvided, IdentityErrorCodes.NoChangesProvided);
    private static readonly Error EmailInvalidFormatError = new("email_invalid_format", "email_invalid_format");

    private readonly IRepository<User, Guid> _userRepository;
    private readonly IUserRoleReader _userRoleReader;
    private readonly ISecurityAuditWriter _auditWriter;
    private readonly IIntegrationEventCollector _eventCollector;
    private readonly TimeProvider _timeProvider;

    public UpdateUserCommandHandler(
        IRepository<User, Guid> userRepository,
        IUserRoleReader userRoleReader,
        ISecurityAuditWriter auditWriter,
        IIntegrationEventCollector eventCollector,
        TimeProvider timeProvider)
    {
        _userRepository = userRepository;
        _userRoleReader = userRoleReader;
        _auditWriter = auditWriter;
        _eventCollector = eventCollector;
        _timeProvider = timeProvider;
    }

    public async ValueTask<Result<UserResult>> Handle(UpdateUserCommand command, CancellationToken cancellationToken)
    {
        if (command.FullName is null && command.Email is null)
            return Result.Failure<UserResult>(NoChangesProvidedError);

        var targetUser = await _userRepository.GetByIdAsync(command.TargetUserId, cancellationToken);
        if (targetUser is null)
            return Result.Failure<UserResult>(UserNotFoundError);

        Email? newEmail = null;
        if (command.Email is not null)
        {
            try
            {
                newEmail = Email.Create(command.Email);
            }
            catch (ArgumentException)
            {
                return Result.Failure<UserResult>(EmailInvalidFormatError);
            }
        }

        var now = _timeProvider.GetUtcNow();
        var changedFields = new List<string>();

        if (command.FullName is not null && !string.Equals(command.FullName.Trim(), targetUser.FullName, StringComparison.Ordinal))
        {
            targetUser.ChangeFullName(command.FullName, now);
            changedFields.Add("full_name");
        }

        if (newEmail is not null && !string.Equals(newEmail.NormalizedValue, targetUser.NormalizedEmail, StringComparison.Ordinal))
        {
            targetUser.ChangeEmail(newEmail, now);
            changedFields.Add("email");
        }

        if (changedFields.Count > 0)
        {
            var correlationId = Guid.NewGuid();

            _auditWriter.Record(SecurityAuditEntry.Record(
                Guid.NewGuid(), command.TenantId, SecurityAuditEventType.UserUpdated, now, correlationId,
                reasonCode: null, userId: command.TargetUserId, sessionId: null, refreshTokenId: null, ipAddress: null));

            _eventCollector.Enqueue(new UserUpdated
            {
                TenantId = command.TenantId,
                AggregateId = command.TargetUserId,
                AggregateType = "User",
                CorrelationId = correlationId,
                ActorType = "User",
                ActorId = command.ActorId.ToString(),
                ChangedFields = changedFields,
            });
        }

        var roleCodes = await _userRoleReader.GetRoleCodesAsync(command.TargetUserId, cancellationToken);
        var result = new UserResult(
            targetUser.Id, targetUser.FullName, targetUser.Email.Value, targetUser.Status.ToString(),
            roleCodes, targetUser.CreatedAt, targetUser.LastLoginAt);

        return Result.Success(result);
    }
}
