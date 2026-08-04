using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.PropertyManagement.Application.Condominiums;
using IHostPro.Contexts.PropertyManagement.Application.Errors;
using IHostPro.Contexts.PropertyManagement.Contracts;
using IHostPro.Contexts.PropertyManagement.Domain;
using IHostPro.Contexts.PropertyManagement.Domain.Enums;
using IHostPro.Contexts.PropertyManagement.Domain.ValueObjects;

namespace IHostPro.Contexts.PropertyManagement.Application.Properties;

/// <summary>
/// Updates a property's code, name, capacity, condominium link and/or own
/// address, idempotently (Checkpoint 3 plan, item 4/5). Runs inside the
/// transaction <see cref="IUpdatePropertyExecutor"/> opens for this command —
/// never calls <c>SaveChangesAsync</c> itself.
///
/// Validates the FINAL combined state (after conceptually applying every
/// supplied field) has an effective address source before mutating anything
/// (Checkpoint 3 plan, item 4/7): own address, or a linked condominium.
/// Idempotency: each supplied field is compared, after the SAME
/// normalization the domain itself applies, against the property's CURRENT
/// value — only fields that genuinely change are applied, recorded in
/// <see cref="PropertyUpdated.ChangedFields"/> (fixed order: <c>"code"</c>,
/// <c>"name"</c>, <c>"capacity"</c>, <c>"condominium_id"</c>,
/// <c>"address"</c>), and cause an audit entry/event to be emitted at all —
/// a no-op request still returns <c>200</c> with the current state, but
/// stages no mutation.
///
/// An <c>Archived</c> property rejects every PATCH outright — including a
/// values-already-match no-op — before any diffing (Checkpoint 4 plan, item
/// 6): archival is meant to freeze the record, not merely block genuine
/// changes.
/// </summary>
public sealed class UpdatePropertyCommandHandler : ICommandHandler<UpdatePropertyCommand, PropertyResult>
{
    private static readonly Error PropertyNotFoundError = new(
        PropertyManagementErrorCodes.PropertyNotFound, PropertyManagementErrorCodes.PropertyNotFound);
    private static readonly Error NoChangesProvidedError = new(
        PropertyManagementErrorCodes.NoChangesProvided, PropertyManagementErrorCodes.NoChangesProvided);
    private static readonly Error PropertyAddressRequiredError = new(
        PropertyManagementErrorCodes.PropertyAddressRequired, PropertyManagementErrorCodes.PropertyAddressRequired);
    private static readonly Error CondominiumNotFoundError = new(
        PropertyManagementErrorCodes.CondominiumNotFound, PropertyManagementErrorCodes.CondominiumNotFound);
    private static readonly Error AddressInvalidError = new("property_address_invalid", "property_address_invalid");
    private static readonly Error CodeInvalidError = new("property_code_invalid", "property_code_invalid");
    private static readonly Error ArchivedPropertyCannotBeModifiedError = new(
        PropertyManagementErrorCodes.ArchivedPropertyCannotBeModified, PropertyManagementErrorCodes.ArchivedPropertyCannotBeModified);

    private readonly IRepository<Property, Guid> _repository;
    private readonly ICondominiumReader _condominiumReader;
    private readonly IPropertyAuditWriter _auditWriter;
    private readonly IIntegrationEventCollector _eventCollector;
    private readonly TimeProvider _timeProvider;

    public UpdatePropertyCommandHandler(
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

    public async ValueTask<Result<PropertyResult>> Handle(UpdatePropertyCommand command, CancellationToken cancellationToken)
    {
        if (!command.Code.IsSet && !command.Name.IsSet && !command.Capacity.IsSet &&
            !command.CondominiumId.IsSet && !command.Address.IsSet)
        {
            return Result.Failure<PropertyResult>(NoChangesProvidedError);
        }

        var property = await _repository.GetByIdAsync(command.PropertyId, cancellationToken);
        if (property is null)
            return Result.Failure<PropertyResult>(PropertyNotFoundError);

        // Checked before any diffing/no-op evaluation (Checkpoint 4 plan,
        // item 6) — even a PATCH whose supplied values already match the
        // current ones must be rejected when the property is Archived.
        if (property.Status == PropertyStatus.Archived)
            return Result.Failure<PropertyResult>(ArchivedPropertyCannotBeModifiedError);

        // Prospective final CondominiumId: omitted keeps the current link;
        // explicit null removes it; a supplied id reassigns it.
        var finalCondominiumId = command.CondominiumId.IsSet ? command.CondominiumId.Value : property.CondominiumId;

        // Prospective final own Address: omitted keeps the current one;
        // explicit null removes it; a supplied object replaces it wholesale.
        Address? finalAddress;
        if (command.Address.IsSet)
        {
            if (command.Address.Value is null)
            {
                finalAddress = null;
            }
            else
            {
                try
                {
                    finalAddress = Address.Create(
                        command.Address.Value.ZipCode, command.Address.Value.Street, command.Address.Value.Number,
                        command.Address.Value.Complement, command.Address.Value.Neighborhood, command.Address.Value.City,
                        command.Address.Value.State, command.Address.Value.Country);
                }
                catch (ArgumentException)
                {
                    return Result.Failure<PropertyResult>(AddressInvalidError);
                }
            }
        }
        else
        {
            finalAddress = property.Address;
        }

        if (finalAddress is null && finalCondominiumId is null)
            return Result.Failure<PropertyResult>(PropertyAddressRequiredError);

        // Resolves the condominium's address for the response's
        // EffectiveAddress, validating existence only when the link is
        // actually being reassigned — an unchanged link was already proven
        // valid by the composite tenant-aware FK at the time it was set.
        AddressResult? condominiumAddress = null;
        if (command.CondominiumId.IsSet && command.CondominiumId.Value is Guid reassignedCondominiumId)
        {
            condominiumAddress = await _condominiumReader.GetAddressByIdAsync(reassignedCondominiumId, cancellationToken);
            if (condominiumAddress is null)
                return Result.Failure<PropertyResult>(CondominiumNotFoundError);
        }
        else if (finalAddress is null && finalCondominiumId is Guid unchangedCondominiumId)
        {
            condominiumAddress = await _condominiumReader.GetAddressByIdAsync(unchangedCondominiumId, cancellationToken);
            if (condominiumAddress is null)
                return Result.Failure<PropertyResult>(CondominiumNotFoundError);
        }

        PropertyCode? newCode = null;
        if (command.Code.IsSet)
        {
            try
            {
                newCode = PropertyCode.Create(command.Code.Value!);
            }
            catch (ArgumentException)
            {
                return Result.Failure<PropertyResult>(CodeInvalidError);
            }
        }

        var now = _timeProvider.GetUtcNow();
        var changedFields = new List<string>();

        if (newCode is not null && !string.Equals(newCode.NormalizedValue, property.NormalizedCode, StringComparison.Ordinal))
        {
            property.ChangeCode(newCode, now);
            changedFields.Add("code");
        }

        if (command.Name.IsSet && !string.Equals(command.Name.Value!.Trim(), property.Name, StringComparison.Ordinal))
        {
            property.Rename(command.Name.Value!, now);
            changedFields.Add("name");
        }

        if (command.Capacity.IsSet && command.Capacity.Value is int newCapacity && newCapacity != property.Capacity)
        {
            property.ChangeCapacity(newCapacity, now);
            changedFields.Add("capacity");
        }

        if (command.CondominiumId.IsSet && command.CondominiumId.Value != property.CondominiumId)
        {
            property.ChangeCondominium(command.CondominiumId.Value, now);
            changedFields.Add("condominium_id");
        }

        if (command.Address.IsSet && finalAddress != property.Address)
        {
            property.ChangeAddress(finalAddress, now);
            changedFields.Add("address");
        }

        if (changedFields.Count > 0)
        {
            var correlationId = Guid.NewGuid();

            _auditWriter.Record(PropertyAuditEntry.Create(
                Guid.NewGuid(), command.TenantId, command.ActorId, "Property", command.PropertyId,
                "property_updated", changedFields, now));

            _eventCollector.Enqueue(new PropertyUpdated
            {
                TenantId = command.TenantId,
                AggregateId = command.PropertyId,
                AggregateType = "Property",
                CorrelationId = correlationId,
                ActorType = "User",
                ActorId = command.ActorId.ToString(),
                PropertyId = command.PropertyId,
                ChangedFields = changedFields,
            });
        }

        var ownAddressResult = property.Address is not null ? AddressResultMapper.ToResult(property.Address) : null;
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
            PropertyStatusCodeMapper.ToCode(property.Status),
            property.CreatedAt,
            property.UpdatedAt);

        return Result.Success(result);
    }
}
