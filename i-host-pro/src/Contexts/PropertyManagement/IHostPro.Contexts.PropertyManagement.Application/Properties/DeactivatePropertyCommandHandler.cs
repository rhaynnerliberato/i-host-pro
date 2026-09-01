using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.PropertyManagement.Application.Condominiums;
using IHostPro.Contexts.PropertyManagement.Application.Errors;
using IHostPro.Contexts.PropertyManagement.Contracts;
using IHostPro.Contexts.PropertyManagement.Domain;
using IHostPro.Contexts.PropertyManagement.Domain.Enums;

namespace IHostPro.Contexts.PropertyManagement.Application.Properties;

/// <summary>
/// Deactivates a property (Checkpoint 4 plan, item 3/4). Runs inside the
/// transaction <see cref="ILifecyclePropertyExecutor"/> opens for this
/// command — never calls <c>SaveChangesAsync</c> itself.
///
/// Only <c>Active</c> succeeds: <c>Inactive</c> rejects as already-inactive,
/// <c>Draft</c> rejects as an invalid transition (distinct stable codes —
/// Checkpoint 4 plan, item 4), <c>Archived</c> rejects as already-archived.
/// No address precondition — unlike Activate, Deactivate never needs one.
/// </summary>
public sealed class DeactivatePropertyCommandHandler : ICommandHandler<DeactivatePropertyCommand, PropertyResult>
{
    private static readonly Error PropertyNotFoundError = new(
        PropertyManagementErrorCodes.PropertyNotFound, PropertyManagementErrorCodes.PropertyNotFound);
    private static readonly Error PropertyAlreadyInactiveError = new(
        PropertyManagementErrorCodes.PropertyAlreadyInactive, PropertyManagementErrorCodes.PropertyAlreadyInactive);
    private static readonly Error InvalidPropertyStatusTransitionError = new(
        PropertyManagementErrorCodes.InvalidPropertyStatusTransition, PropertyManagementErrorCodes.InvalidPropertyStatusTransition);
    private static readonly Error PropertyAlreadyArchivedError = new(
        PropertyManagementErrorCodes.PropertyAlreadyArchived, PropertyManagementErrorCodes.PropertyAlreadyArchived);

    private readonly IRepository<Property, Guid> _repository;
    private readonly ICondominiumReader _condominiumReader;
    private readonly IPropertyAuditWriter _auditWriter;
    private readonly IIntegrationEventCollector _eventCollector;
    private readonly TimeProvider _timeProvider;

    public DeactivatePropertyCommandHandler(
        IRepository<Property, Guid> repository,
        ICondominiumReader condominiumReader,
        IPropertyAuditWriter auditWriter,
        IIntegrationEventCollector eventCollector,
        TimeProvider timeProvider)
    {
        _repository = repository;
        _condominiumReader = condominiumReader;
        _auditWriter = auditWriter;
        _eventCollector = eventCollector;
        _timeProvider = timeProvider;
    }

    public async ValueTask<Result<PropertyResult>> Handle(DeactivatePropertyCommand command, CancellationToken cancellationToken)
    {
        var property = await _repository.GetByIdAsync(command.PropertyId, cancellationToken);
        if (property is null)
            return Result.Failure<PropertyResult>(PropertyNotFoundError);

        switch (property.Status)
        {
            case PropertyStatus.Inactive:
                return Result.Failure<PropertyResult>(PropertyAlreadyInactiveError);
            case PropertyStatus.Draft:
                return Result.Failure<PropertyResult>(InvalidPropertyStatusTransitionError);
            case PropertyStatus.Archived:
                return Result.Failure<PropertyResult>(PropertyAlreadyArchivedError);
        }

        var addressResolution = await PropertyEffectiveAddressResolver.ResolveAsync(property, _condominiumReader, cancellationToken);
        if (addressResolution.IsFailure)
            return Result.Failure<PropertyResult>(addressResolution.Error);

        var now = _timeProvider.GetUtcNow();
        property.Deactivate(now);

        var correlationId = Guid.NewGuid();

        _auditWriter.Record(PropertyAuditEntry.Create(
            Guid.NewGuid(), command.TenantId, command.ActorId, "Property", command.PropertyId,
            "property_deactivated", ["status"], now));

        _eventCollector.Enqueue(new PropertyDeactivated
        {
            TenantId = command.TenantId,
            AggregateId = command.PropertyId,
            AggregateType = "Property",
            CorrelationId = correlationId,
            ActorType = "User",
            ActorId = command.ActorId.ToString(),
            PropertyId = command.PropertyId,
        });

        var (ownAddress, effectiveAddress, effectiveAddressSource) = addressResolution.Value;

        var result = new PropertyResult(
            property.Id,
            property.Code.Value,
            property.Name,
            property.Capacity,
            property.CondominiumId,
            ownAddress,
            effectiveAddress,
            effectiveAddressSource,
            property.TimeZoneId,
            PropertyStatusCodeMapper.ToCode(property.Status),
            property.CreatedAt,
            property.UpdatedAt);

        return Result.Success(result);
    }
}
