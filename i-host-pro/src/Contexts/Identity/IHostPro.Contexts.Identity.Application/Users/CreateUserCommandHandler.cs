using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.Identity.Application.Catalog;
using IHostPro.Contexts.Identity.Application.Errors;
using IHostPro.Contexts.Identity.Contracts;
using IHostPro.Contexts.Identity.Domain;
using IHostPro.Contexts.Identity.Domain.Enums;
using IHostPro.Contexts.Identity.Domain.ValueObjects;

namespace IHostPro.Contexts.Identity.Application.Users;

/// <summary>
/// Creates a user with exactly one mandatory initial role, atomically with
/// its audit trail and the two Integration Events it publishes (Incremento
/// 3, Checkpoint 5). Runs inside the transaction <see cref="ICreateUserExecutor"/>
/// opens for this command — never calls <c>SaveChangesAsync</c> itself; every
/// mutation below is staged only.
///
/// Validation order — role existence, then email format, then password
/// policy — all run BEFORE any mutation is staged, so a rejection on any of
/// them creates no <see cref="Domain.User"/>, no <see cref="UserRole"/>, no
/// audit entry and no event (Section 5). Email uniqueness is deliberately
/// NOT pre-checked here beyond what the persisted unique index enforces —
/// see <see cref="ICreateUserExecutor"/>'s own doc comment.
/// </summary>
public sealed class CreateUserCommandHandler : ICommandHandler<CreateUserCommand, UserResult>
{
    private static readonly Error RoleNotFoundError = new(IdentityErrorCodes.RoleNotFound, IdentityErrorCodes.RoleNotFound);

    private readonly IIdentityCatalogReader _catalogReader;
    private readonly IUserProvisioningService _provisioningService;
    private readonly IRepository<User, Guid> _userRepository;
    private readonly IUserRoleWriter _userRoleWriter;
    private readonly ISecurityAuditWriter _auditWriter;
    private readonly IIntegrationEventCollector _eventCollector;
    private readonly TimeProvider _timeProvider;

    public CreateUserCommandHandler(
        IIdentityCatalogReader catalogReader,
        IUserProvisioningService provisioningService,
        IRepository<User, Guid> userRepository,
        IUserRoleWriter userRoleWriter,
        ISecurityAuditWriter auditWriter,
        IIntegrationEventCollector eventCollector,
        TimeProvider timeProvider)
    {
        _catalogReader = catalogReader;
        _provisioningService = provisioningService;
        _userRepository = userRepository;
        _userRoleWriter = userRoleWriter;
        _auditWriter = auditWriter;
        _eventCollector = eventCollector;
        _timeProvider = timeProvider;
    }

    public async ValueTask<Result<UserResult>> Handle(CreateUserCommand command, CancellationToken cancellationToken)
    {
        var roles = await _catalogReader.ListRolesAsync(cancellationToken);
        if (!roles.Any(r => string.Equals(r.Code, command.RoleCode, StringComparison.Ordinal)))
            return Result.Failure<UserResult>(RoleNotFoundError);

        Email email;
        try
        {
            email = Email.Create(command.Email);
        }
        catch (ArgumentException)
        {
            return Result.Failure<UserResult>(new Error("email_invalid_format", "email_invalid_format"));
        }

        var passwordCheck = await _provisioningService.ValidatePasswordAsync(command.InitialPassword);
        if (!passwordCheck.Succeeded)
        {
            return Result.Failure<UserResult>(
                new Error(string.Join(",", passwordCheck.ErrorCodes), "password_policy_violation"));
        }

        var now = _timeProvider.GetUtcNow();
        var passwordHash = _provisioningService.HashPassword(command.InitialPassword);

        var user = User.Register(Guid.NewGuid(), command.TenantId, email, command.FullName, passwordHash, now);
        _userRepository.Add(user);

        var userRole = new UserRole(command.TenantId, user.Id, command.RoleCode, now, command.ActorId);
        _userRoleWriter.Assign(userRole);

        var correlationId = Guid.NewGuid();

        _auditWriter.Record(SecurityAuditEntry.Record(
            Guid.NewGuid(), command.TenantId, SecurityAuditEventType.UserCreated, now, correlationId,
            reasonCode: null, userId: user.Id, actorId: command.ActorId, sessionId: null, refreshTokenId: null, ipAddress: null));

        _auditWriter.Record(SecurityAuditEntry.Record(
            Guid.NewGuid(), command.TenantId, SecurityAuditEventType.UserRoleAssigned, now, correlationId,
            reasonCode: null, userId: user.Id, actorId: command.ActorId, sessionId: null, refreshTokenId: null, ipAddress: null));

        var userCreated = new UserCreated
        {
            TenantId = command.TenantId,
            AggregateId = user.Id,
            AggregateType = "User",
            CorrelationId = correlationId,
            ActorType = "User",
            ActorId = command.ActorId.ToString(),
        };
        _eventCollector.Enqueue(userCreated);

        // CausationId chains to UserCreated.EventId — same convention as
        // Logout's UserLoggedOut -> SessionRevoked pair (Documento 07 §13.3).
        _eventCollector.Enqueue(new UserRoleAssigned
        {
            TenantId = command.TenantId,
            AggregateId = user.Id,
            AggregateType = "User",
            CorrelationId = correlationId,
            CausationId = userCreated.EventId,
            ActorType = "User",
            ActorId = command.ActorId.ToString(),
            RoleCode = command.RoleCode,
        });

        var result = new UserResult(
            user.Id, user.FullName, user.Email.Value, user.Status.ToString(), [command.RoleCode], user.CreatedAt, user.LastLoginAt);

        return Result.Success(result);
    }
}
