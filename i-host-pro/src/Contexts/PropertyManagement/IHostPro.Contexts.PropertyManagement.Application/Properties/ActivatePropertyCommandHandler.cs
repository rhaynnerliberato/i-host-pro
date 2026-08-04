using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.PropertyManagement.Application.Condominiums;
using IHostPro.Contexts.PropertyManagement.Application.Errors;
using IHostPro.Contexts.PropertyManagement.Contracts;
using IHostPro.Contexts.PropertyManagement.Domain;
using IHostPro.Contexts.PropertyManagement.Domain.Enums;

namespace IHostPro.Contexts.PropertyManagement.Application.Properties;

/// <summary>
/// Activates a property (Checkpoint 4 plan, item 3/5). Runs inside the
/// transaction <see cref="ILifecyclePropertyExecutor"/> opens for this
/// command — never calls <c>SaveChangesAsync</c> itself.
///
/// Rejects <c>Active</c> (<see cref="PropertyManagementErrorCodes.PropertyAlreadyActive"/>)
/// and <c>Archived</c> (<see cref="PropertyManagementErrorCodes.PropertyAlreadyArchived"/>)
/// before validating anything else — only <c>Draft</c>/<c>Inactive</c>
/// proceed. Before activating, confirms an effective address exists (own, or
/// the linked condominium's, re-validated to still exist in the same tenant)
/// — the only lifecycle transition with this precondition (Checkpoint 4
/// plan, item 5).
/// </summary>
public sealed class ActivatePropertyCommandHandler : ICommandHandler<ActivatePropertyCommand, PropertyResult>
{
    private static readonly Error PropertyNotFoundError = new(
        PropertyManagementErrorCodes.PropertyNotFound, PropertyManagementErrorCodes.PropertyNotFound);
    private static readonly Error PropertyAlreadyActiveError = new(
        PropertyManagementErrorCodes.PropertyAlreadyActive, PropertyManagementErrorCodes.PropertyAlreadyActive);
    private static readonly Error PropertyAlreadyArchivedError = new(
        PropertyManagementErrorCodes.PropertyAlreadyArchived, PropertyManagementErrorCodes.PropertyAlreadyArchived);

    private readonly IRepository<Property, Guid> _repository;
    private readonly ICondominiumReader _condominiumReader;
    private readonly IPropertyAuditWriter _auditWriter;
    private readonly IIntegrationEventCollector _eventCollector;
    private readonly TimeProvider _timeProvider;

    public ActivatePropertyCommandHandler(
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

    public async ValueTask<Result<PropertyResult>> Handle(ActivatePropertyCommand command, CancellationToken cancellationToken)
    {
        var property = await _repository.GetByIdAsync(command.PropertyId, cancellationToken);
        if (property is null)
            return Result.Failure<PropertyResult>(PropertyNotFoundError);

        switch (property.Status)
        {
            case PropertyStatus.Active:
                return Result.Failure<PropertyResult>(PropertyAlreadyActiveError);
            case PropertyStatus.Archived:
                return Result.Failure<PropertyResult>(PropertyAlreadyArchivedError);
        }

        var addressResolution = await PropertyEffectiveAddressResolver.ResolveAsync(property, _condominiumReader, cancellationToken);
        if (addressResolution.IsFailure)
            return Result.Failure<PropertyResult>(addressResolution.Error);

        var now = _timeProvider.GetUtcNow();
        property.Activate(now);

        var correlationId = Guid.NewGuid();

        _auditWriter.Record(PropertyAuditEntry.Create(
            Guid.NewGuid(), command.TenantId, command.ActorId, "Property", command.PropertyId,
            "property_activated", ["status"], now));

        _eventCollector.Enqueue(new PropertyActivated
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
            PropertyStatusCodeMapper.ToCode(property.Status),
            property.CreatedAt,
            property.UpdatedAt);

        return Result.Success(result);
    }
}
