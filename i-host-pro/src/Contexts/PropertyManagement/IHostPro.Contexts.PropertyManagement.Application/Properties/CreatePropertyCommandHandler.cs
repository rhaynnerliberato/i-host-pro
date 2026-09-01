using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.PropertyManagement.Application.Condominiums;
using IHostPro.Contexts.PropertyManagement.Application.Errors;
using IHostPro.Contexts.PropertyManagement.Contracts;
using IHostPro.Contexts.PropertyManagement.Domain;
using IHostPro.Contexts.PropertyManagement.Domain.ValueObjects;

namespace IHostPro.Contexts.PropertyManagement.Application.Properties;

/// <summary>
/// Creates a property, born in <c>PropertyStatus.Draft</c> (Checkpoint 3
/// plan, item 3). Runs inside the transaction <see cref="ICreatePropertyExecutor"/>
/// opens for this command — never calls <c>SaveChangesAsync</c> itself. Code
/// format is enforced entirely by <c>PropertyCode.Create</c>, address format
/// by <c>Address.Create</c> — never duplicated in the validator (mirrors
/// <c>CreateCondominiumCommandHandler</c>'s own reuse). Never auto-copies the
/// condominium's address into the property's own row (Checkpoint 3 plan,
/// item 9) — only reads it to resolve <see cref="PropertyResult.EffectiveAddress"/>
/// for the response.
/// </summary>
public sealed class CreatePropertyCommandHandler : ICommandHandler<CreatePropertyCommand, PropertyResult>
{
    private static readonly Error AddressInvalidError = new("property_address_invalid", "property_address_invalid");
    private static readonly Error CodeInvalidError = new("property_code_invalid", "property_code_invalid");
    private static readonly Error NameInvalidError = new("property_name_invalid", "property_name_invalid");

    private static readonly Error PropertyAddressRequiredError = new(
        PropertyManagementErrorCodes.PropertyAddressRequired, PropertyManagementErrorCodes.PropertyAddressRequired);

    private static readonly Error CondominiumNotFoundError = new(
        PropertyManagementErrorCodes.CondominiumNotFound, PropertyManagementErrorCodes.CondominiumNotFound);

    private readonly IRepository<Property, Guid> _repository;
    private readonly ICondominiumReader _condominiumReader;
    private readonly IPropertyAuditWriter _auditWriter;
    private readonly IIntegrationEventCollector _eventCollector;
    private readonly TimeProvider _timeProvider;

    public CreatePropertyCommandHandler(
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

    public async ValueTask<Result<PropertyResult>> Handle(CreatePropertyCommand command, CancellationToken cancellationToken)
    {
        // command.CondominiumId/command.Address: at least one must resolve an
        // effective address. Checked here, before any lookup, since the rule
        // does not depend on the condominium actually existing.
        if (command.CondominiumId is null && command.Address is null)
            return Result.Failure<PropertyResult>(PropertyAddressRequiredError);

        AddressResult? condominiumAddress = null;
        if (command.CondominiumId is Guid condominiumId)
        {
            condominiumAddress = await _condominiumReader.GetAddressByIdAsync(condominiumId, cancellationToken);
            if (condominiumAddress is null)
                return Result.Failure<PropertyResult>(CondominiumNotFoundError);
        }

        PropertyCode code;
        try
        {
            code = PropertyCode.Create(command.Code);
        }
        catch (ArgumentException)
        {
            return Result.Failure<PropertyResult>(CodeInvalidError);
        }

        Address? address = null;
        if (command.Address is not null)
        {
            try
            {
                address = Address.Create(
                    command.Address.ZipCode, command.Address.Street, command.Address.Number,
                    command.Address.Complement, command.Address.Neighborhood, command.Address.City,
                    command.Address.State, command.Address.Country);
            }
            catch (ArgumentException)
            {
                return Result.Failure<PropertyResult>(AddressInvalidError);
            }
        }

        var now = _timeProvider.GetUtcNow();
        var propertyId = Guid.NewGuid();

        Property property;
        try
        {
            property = Property.Create(
                propertyId, command.TenantId, code, command.Name, command.Capacity,
                command.CondominiumId, address, now);
        }
        catch (ArgumentException)
        {
            return Result.Failure<PropertyResult>(NameInvalidError);
        }

        _repository.Add(property);

        var correlationId = Guid.NewGuid();

        _auditWriter.Record(PropertyAuditEntry.Create(
            Guid.NewGuid(), command.TenantId, command.ActorId, "Property", propertyId,
            "property_created", changedFields: [], now));

        _eventCollector.Enqueue(new PropertyCreated
        {
            TenantId = command.TenantId,
            AggregateId = propertyId,
            AggregateType = "Property",
            CorrelationId = correlationId,
            ActorType = "User",
            ActorId = command.ActorId.ToString(),
            PropertyId = propertyId,
            Status = PropertyStatusCodeMapper.ToCode(property.Status),
        });

        var ownAddressResult = address is not null ? AddressResultMapper.ToResult(address) : null;
        var effectiveAddress = ownAddressResult ?? condominiumAddress!;
        var effectiveAddressSource = ownAddressResult is not null ? "property" : "condominium";

        var result = new PropertyResult(
            property.Id,
            property.Code.Value,
            property.Name,
            property.Capacity,
            property.CondominiumId,
            ownAddressResult,
            effectiveAddress,
            effectiveAddressSource,
            property.TimeZoneId,
            PropertyStatusCodeMapper.ToCode(property.Status),
            property.CreatedAt,
            property.UpdatedAt);

        return Result.Success(result);
    }
}
