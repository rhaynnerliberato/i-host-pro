using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.Identity.Contracts;
using IHostPro.Contexts.Identity.Contracts.Authorization;
using IHostPro.Contexts.PropertyManagement.Application.Errors;
using IHostPro.Contexts.PropertyManagement.Contracts;
using IHostPro.Contexts.PropertyManagement.Domain;

namespace IHostPro.Contexts.PropertyManagement.Application.Owners;

/// <summary>
/// Links a user as a property's owner (Checkpoint 5 plan, item 3/6/7). Does
/// NOT run inside Property Management's write transaction for its entire
/// duration — the eligibility check against Identity happens first, using
/// its own independent connection, which is fully closed before
/// <see cref="ILinkPropertyOwnerExecutor"/> ever opens Property Management's
/// transaction (item 6: no distributed transaction, no PM transaction held
/// open across the Identity call). The resulting TOCTOU window (the owner's
/// role/status could change between the eligibility check and the commit)
/// is explicitly accepted (item 6): the link still confirms, RBAC continues
/// to gate actual access to <c>mine</c> afterward, and no retry of the
/// eligibility check is performed.
/// </summary>
public sealed class LinkPropertyOwnerCommandHandler : ICommandHandler<LinkPropertyOwnerCommand, PropertyOwnerResult>
{
    private static readonly Error OwnerUserNotFoundError = new(
        PropertyManagementErrorCodes.OwnerUserNotFound, PropertyManagementErrorCodes.OwnerUserNotFound);
    private static readonly Error OwnerUserNotEligibleError = new(
        PropertyManagementErrorCodes.OwnerUserNotEligible, PropertyManagementErrorCodes.OwnerUserNotEligible);
    private static readonly Error PropertyNotFoundError = new(
        PropertyManagementErrorCodes.PropertyNotFound, PropertyManagementErrorCodes.PropertyNotFound);
    private static readonly Error PropertyOwnerAlreadyLinkedError = new(
        PropertyManagementErrorCodes.PropertyOwnerAlreadyLinked, PropertyManagementErrorCodes.PropertyOwnerAlreadyLinked);

    private readonly IIdentityUserEligibilityReader _eligibilityReader;
    private readonly ILinkPropertyOwnerExecutor _executor;
    private readonly IRepository<Property, Guid> _propertyRepository;
    private readonly IPropertyOwnerReader _ownerReader;
    private readonly IPropertyOwnerWriter _ownerWriter;
    private readonly IPropertyAuditWriter _auditWriter;
    private readonly IIntegrationEventCollector _eventCollector;
    private readonly TimeProvider _timeProvider;

    public LinkPropertyOwnerCommandHandler(
        IIdentityUserEligibilityReader eligibilityReader,
        ILinkPropertyOwnerExecutor executor,
        IRepository<Property, Guid> propertyRepository,
        IPropertyOwnerReader ownerReader,
        IPropertyOwnerWriter ownerWriter,
        IPropertyAuditWriter auditWriter,
        IIntegrationEventCollector eventCollector,
        TimeProvider timeProvider)
    {
        _eligibilityReader = eligibilityReader;
        _executor = executor;
        _propertyRepository = propertyRepository;
        _ownerReader = ownerReader;
        _ownerWriter = ownerWriter;
        _auditWriter = auditWriter;
        _eventCollector = eventCollector;
        _timeProvider = timeProvider;
    }

    public async ValueTask<Result<PropertyOwnerResult>> Handle(LinkPropertyOwnerCommand command, CancellationToken cancellationToken)
    {
        // Step 1-2 (Checkpoint 5 plan, item 6): validate eligibility, then
        // let the Identity read conclude — no Property Management
        // transaction is open yet.
        var eligibility = await _eligibilityReader.GetAsync(
            command.TenantId, command.OwnerUserId, IdentityRoleCodes.PropertyOwner, cancellationToken);

        if (eligibility is null)
            return Result.Failure<PropertyOwnerResult>(OwnerUserNotFoundError);

        if (!eligibility.IsActive || !eligibility.HasRequiredRole)
            return Result.Failure<PropertyOwnerResult>(OwnerUserNotEligibleError);

        // Step 3-9: only now does Property Management's own write
        // transaction open, via the executor.
        return await _executor.ExecuteAsync(async () =>
        {
            var property = await _propertyRepository.GetByIdAsync(command.PropertyId, cancellationToken);
            if (property is null)
                return Result.Failure<PropertyOwnerResult>(PropertyNotFoundError);

            var alreadyLinked = await _ownerReader.ExistsAsync(command.PropertyId, command.OwnerUserId, cancellationToken);
            if (alreadyLinked)
                return Result.Failure<PropertyOwnerResult>(PropertyOwnerAlreadyLinkedError);

            var now = _timeProvider.GetUtcNow();
            var link = PropertyOwnerLink.Create(
                Guid.NewGuid(), command.TenantId, command.PropertyId, command.OwnerUserId, command.ActorId, now);
            _ownerWriter.Link(link);

            var correlationId = Guid.NewGuid();

            _auditWriter.Record(PropertyAuditEntry.Create(
                Guid.NewGuid(), command.TenantId, command.ActorId, "Property", command.PropertyId,
                "property_owner_linked", ["owner_user_id"], now));

            _eventCollector.Enqueue(new PropertyOwnerLinked
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

            return Result.Success(new PropertyOwnerResult(command.PropertyId, command.OwnerUserId, now));
        }, cancellationToken);
    }
}
