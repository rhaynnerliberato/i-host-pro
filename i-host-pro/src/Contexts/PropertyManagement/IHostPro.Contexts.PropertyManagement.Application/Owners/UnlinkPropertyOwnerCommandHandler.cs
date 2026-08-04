using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.PropertyManagement.Application.Errors;
using IHostPro.Contexts.PropertyManagement.Contracts;
using IHostPro.Contexts.PropertyManagement.Domain;

namespace IHostPro.Contexts.PropertyManagement.Application.Owners;

/// <summary>
/// Removes an owner's link to a property (Checkpoint 5 plan, item 8/16).
/// Runs entirely inside the transaction <see cref="IUnlinkPropertyOwnerExecutor"/>
/// opens for this command — never calls <c>SaveChangesAsync</c> itself. No
/// Identity call, unlike <see cref="LinkPropertyOwnerCommandHandler"/> — the
/// whole operation stays within Property Management.
/// </summary>
public sealed class UnlinkPropertyOwnerCommandHandler : ICommandHandler<UnlinkPropertyOwnerCommand>
{
    private static readonly Error PropertyNotFoundError = new(
        PropertyManagementErrorCodes.PropertyNotFound, PropertyManagementErrorCodes.PropertyNotFound);
    private static readonly Error PropertyOwnerNotLinkedError = new(
        PropertyManagementErrorCodes.PropertyOwnerNotLinked, PropertyManagementErrorCodes.PropertyOwnerNotLinked);

    private readonly IRepository<Property, Guid> _propertyRepository;
    private readonly IPropertyOwnerReader _ownerReader;
    private readonly IPropertyOwnerWriter _ownerWriter;
    private readonly IPropertyAuditWriter _auditWriter;
    private readonly IIntegrationEventCollector _eventCollector;
    private readonly TimeProvider _timeProvider;

    public UnlinkPropertyOwnerCommandHandler(
        IRepository<Property, Guid> propertyRepository,
        IPropertyOwnerReader ownerReader,
        IPropertyOwnerWriter ownerWriter,
        IPropertyAuditWriter auditWriter,
        IIntegrationEventCollector eventCollector,
        TimeProvider timeProvider)
    {
        _propertyRepository = propertyRepository;
        _ownerReader = ownerReader;
        _ownerWriter = ownerWriter;
        _auditWriter = auditWriter;
        _eventCollector = eventCollector;
        _timeProvider = timeProvider;
    }

    public async ValueTask<Result> Handle(UnlinkPropertyOwnerCommand command, CancellationToken cancellationToken)
    {
        var property = await _propertyRepository.GetByIdAsync(command.PropertyId, cancellationToken);
        if (property is null)
            return Result.Failure(PropertyNotFoundError);

        var link = await _ownerReader.FindAsync(command.PropertyId, command.OwnerUserId, cancellationToken);
        if (link is null)
            return Result.Failure(PropertyOwnerNotLinkedError);

        _ownerWriter.Unlink(link);

        var now = _timeProvider.GetUtcNow();
        var correlationId = Guid.NewGuid();

        _auditWriter.Record(PropertyAuditEntry.Create(
            Guid.NewGuid(), command.TenantId, command.ActorId, "Property", command.PropertyId,
            "property_owner_unlinked", ["owner_user_id"], now));

        _eventCollector.Enqueue(new PropertyOwnerUnlinked
        {
            TenantId = command.TenantId,
            AggregateId = command.PropertyId,
            AggregateType = "Property",
            CorrelationId = correlationId,
            ActorType = "User",
            ActorId = command.ActorId.ToString(),
            PropertyId = command.PropertyId,
            OwnerUserId = command.OwnerUserId,
        });

        return Result.Success();
    }
}
