using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.PropertyManagement.Application.Errors;
using IHostPro.Contexts.PropertyManagement.Contracts;
using IHostPro.Contexts.PropertyManagement.Domain;
using IHostPro.Contexts.PropertyManagement.Domain.ValueObjects;

namespace IHostPro.Contexts.PropertyManagement.Application.Condominiums;

/// <summary>
/// Updates a condominium's name and/or address, idempotently (Checkpoint 2
/// plan, item 6/7). Runs inside the transaction
/// <see cref="IUpdateCondominiumExecutor"/> opens for this command — never
/// calls <c>SaveChangesAsync</c> itself.
///
/// Idempotency: each supplied field is compared, after the SAME
/// normalization the domain itself applies (<c>Name.Trim()</c>,
/// <c>Address</c>'s Value Object equality), against the condominium's
/// CURRENT value. Only fields that genuinely change are applied to the
/// domain, recorded in <see cref="CondominiumUpdated.ChangedFields"/> (in the
/// fixed order <c>"name"</c> then <c>"address"</c>), and cause an audit
/// entry/event to be emitted at all — a no-op request (every supplied field
/// already matches, or none supplied a change) still returns <c>200</c> with
/// the current state, but stages no mutation: no audit, no event, no bumped
/// <c>UpdatedAt</c>.
/// </summary>
public sealed class UpdateCondominiumCommandHandler : ICommandHandler<UpdateCondominiumCommand, CondominiumResult>
{
    private static readonly Error CondominiumNotFoundError = new(
        PropertyManagementErrorCodes.CondominiumNotFound, PropertyManagementErrorCodes.CondominiumNotFound);
    private static readonly Error NoChangesProvidedError = new(
        PropertyManagementErrorCodes.NoChangesProvided, PropertyManagementErrorCodes.NoChangesProvided);
    private static readonly Error AddressInvalidError = new("condominium_address_invalid", "condominium_address_invalid");

    private readonly IRepository<Condominium, Guid> _repository;
    private readonly IPropertyAuditWriter _auditWriter;
    private readonly IIntegrationEventCollector _eventCollector;
    private readonly TimeProvider _timeProvider;

    public UpdateCondominiumCommandHandler(
        IRepository<Condominium, Guid> repository,
        IPropertyAuditWriter auditWriter,
        IIntegrationEventCollector eventCollector,
        TimeProvider timeProvider)
    {
        _repository = repository;
        _auditWriter = auditWriter;
        _eventCollector = eventCollector;
        _timeProvider = timeProvider;
    }

    public async ValueTask<Result<CondominiumResult>> Handle(UpdateCondominiumCommand command, CancellationToken cancellationToken)
    {
        if (command.Name is null && command.Address is null)
            return Result.Failure<CondominiumResult>(NoChangesProvidedError);

        var condominium = await _repository.GetByIdAsync(command.CondominiumId, cancellationToken);
        if (condominium is null)
            return Result.Failure<CondominiumResult>(CondominiumNotFoundError);

        Address? newAddress = null;
        if (command.Address is not null)
        {
            try
            {
                newAddress = Address.Create(
                    command.Address.ZipCode, command.Address.Street, command.Address.Number,
                    command.Address.Complement, command.Address.Neighborhood, command.Address.City,
                    command.Address.State, command.Address.Country);
            }
            catch (ArgumentException)
            {
                return Result.Failure<CondominiumResult>(AddressInvalidError);
            }
        }

        var now = _timeProvider.GetUtcNow();
        var changedFields = new List<string>();

        if (command.Name is not null && !string.Equals(command.Name.Trim(), condominium.Name, StringComparison.Ordinal))
        {
            condominium.Rename(command.Name, now);
            changedFields.Add("name");
        }

        if (newAddress is not null && newAddress != condominium.Address)
        {
            condominium.ChangeAddress(newAddress, now);
            changedFields.Add("address");
        }

        if (changedFields.Count > 0)
        {
            var correlationId = Guid.NewGuid();

            _auditWriter.Record(PropertyAuditEntry.Create(
                Guid.NewGuid(), command.TenantId, command.ActorId, "Condominium", command.CondominiumId,
                "condominium_updated", changedFields, now));

            _eventCollector.Enqueue(new CondominiumUpdated
            {
                TenantId = command.TenantId,
                AggregateId = command.CondominiumId,
                AggregateType = "Condominium",
                CorrelationId = correlationId,
                ActorType = "User",
                ActorId = command.ActorId.ToString(),
                CondominiumId = command.CondominiumId,
                ChangedFields = changedFields,
            });
        }

        var result = new CondominiumResult(
            condominium.Id,
            condominium.Name,
            AddressResultMapper.ToResult(condominium.Address),
            condominium.CreatedAt,
            condominium.UpdatedAt);

        return Result.Success(result);
    }
}
