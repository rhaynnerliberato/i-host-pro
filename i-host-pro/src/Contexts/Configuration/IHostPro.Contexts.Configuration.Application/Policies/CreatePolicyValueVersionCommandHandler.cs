using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.Configuration.Application.Errors;
using IHostPro.Contexts.Configuration.Contracts;
using IHostPro.Contexts.Configuration.Domain;

namespace IHostPro.Contexts.Configuration.Application.Policies;

/// <summary>
/// Creates a new, current version of a policy value — append-only: the
/// previous current row (if any) is only ever <see cref="PolicyValue.Supersede"/>d,
/// never mutated or removed, in the same transaction as the new row's
/// insert and the new <see cref="PolicyAuditEntry"/> — mirrors
/// <c>CreateReservationCommandHandler</c>'s validate-then-execute shape.
/// </summary>
public sealed class CreatePolicyValueVersionCommandHandler : ICommandHandler<CreatePolicyValueVersionCommand, PolicyValueDetailResult>
{
    private static readonly Error PolicyNotFoundError = new(PolicyErrorCodes.PolicyNotFound, PolicyErrorCodes.PolicyNotFound);
    private static readonly Error VersionConflictError = new(PolicyErrorCodes.VersionConflict, PolicyErrorCodes.VersionConflict);

    private readonly IPolicyDefinitionReader _definitionReader;
    private readonly IPolicyValueRepository _repository;
    private readonly IPolicyAuditWriter _auditWriter;
    private readonly ICreatePolicyValueVersionExecutor _executor;
    private readonly IIntegrationEventCollector _eventCollector;
    private readonly TimeProvider _timeProvider;

    public CreatePolicyValueVersionCommandHandler(
        IPolicyDefinitionReader definitionReader,
        IPolicyValueRepository repository,
        IPolicyAuditWriter auditWriter,
        ICreatePolicyValueVersionExecutor executor,
        IIntegrationEventCollector eventCollector,
        TimeProvider timeProvider)
    {
        _definitionReader = definitionReader;
        _repository = repository;
        _auditWriter = auditWriter;
        _executor = executor;
        _eventCollector = eventCollector;
        _timeProvider = timeProvider;
    }

    public async ValueTask<Result<PolicyValueDetailResult>> Handle(CreatePolicyValueVersionCommand command, CancellationToken cancellationToken)
    {
        if (!await _definitionReader.ExistsAsync(command.PolicyCode, cancellationToken))
            return Result.Failure<PolicyValueDetailResult>(PolicyNotFoundError);

        if (!PolicyScopeParser.TryParse(command.ScopeType, command.PropertyId, out var scope, out var scopeError))
            return Result.Failure<PolicyValueDetailResult>(scopeError!);

        var normalizedValueResult = PolicyValueValidation.ValidateAndNormalize(command.PolicyCode, command.Value);
        if (normalizedValueResult.IsFailure)
            return Result.Failure<PolicyValueDetailResult>(normalizedValueResult.Error);

        return await _executor.ExecuteAsync(async () =>
        {
            var current = await _repository.GetCurrentTrackedAsync(
                command.TenantId, command.PolicyCode, scope.Type, scope.ReferenceId, cancellationToken);

            if (current is null != (command.ExpectedVersion is null))
                return Result.Failure<PolicyValueDetailResult>(VersionConflictError);

            if (current is not null && command.ExpectedVersion != current.Version)
                return Result.Failure<PolicyValueDetailResult>(VersionConflictError);

            var now = _timeProvider.GetUtcNow();
            var newId = Guid.NewGuid();

            var newValue = current is null
                ? PolicyValue.CreateInitialVersion(
                    newId, command.TenantId, command.PolicyCode, scope,
                    normalizedValueResult.Value, now, command.ActorId, command.Reason)
                : PolicyValue.CreateNextVersion(
                    newId, command.TenantId, command.PolicyCode, scope, PolicyVersion.Create(current.Version),
                    normalizedValueResult.Value, now, command.ActorId, command.Reason);

            current?.Supersede();
            _repository.Add(newValue);

            _auditWriter.Record(PolicyAuditEntry.Create(
                Guid.NewGuid(), command.TenantId, command.PolicyCode, scope,
                current?.Version, newValue.Version, current?.Value, newValue.Value,
                command.ActorId, now, command.Reason, origin: "Api", sessionId: null, ipAddress: command.IpAddress));

            _eventCollector.Enqueue(new PolicyUpdated
            {
                TenantId = command.TenantId,
                AggregateId = newValue.Id,
                AggregateType = "PolicyValue",
                CorrelationId = Guid.NewGuid(),
                ActorType = "User",
                ActorId = command.ActorId.ToString(),
                PolicyCode = command.PolicyCode,
                ScopeType = scope.Type.ToString(),
                ScopeReferenceId = scope.ReferenceId,
                PolicyVersion = newValue.Version,
            });

            return Result.Success(ToDetailResult(newValue));
        }, cancellationToken);
    }

    internal static PolicyValueDetailResult ToDetailResult(PolicyValue value) => new(
        value.Id, value.PolicyCode, value.ScopeType.ToString(), value.ScopeReferenceId, value.Version,
        value.Value, value.CreatedAtUtc, value.CreatedByUserId, value.Reason, value.IsCurrent);
}
