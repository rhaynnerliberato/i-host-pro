using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.PropertyManagement.Contracts;
using IHostPro.Contexts.PropertyManagement.Domain;
using IHostPro.Contexts.PropertyManagement.Domain.ValueObjects;

namespace IHostPro.Contexts.PropertyManagement.Application.Condominiums;

/// <summary>
/// Creates a condominium (Checkpoint 2 plan, item 4). Runs inside the
/// transaction <see cref="IPropertyManagementTransactionExecutor"/> opens for
/// this command — never calls <c>SaveChangesAsync</c> itself. Address format
/// (zip code shape, field lengths) is enforced entirely by
/// <c>Address.Create</c>, never duplicated in the validator (mirrors
/// <c>CreateUserCommandHandler</c>'s <c>Email.Create</c> reuse).
/// </summary>
public sealed class CreateCondominiumCommandHandler : ICommandHandler<CreateCondominiumCommand, CondominiumResult>
{
    private static readonly Error AddressInvalidError = new("condominium_address_invalid", "condominium_address_invalid");

    private readonly IRepository<Condominium, Guid> _repository;
    private readonly IPropertyAuditWriter _auditWriter;
    private readonly IIntegrationEventCollector _eventCollector;
    private readonly TimeProvider _timeProvider;

    public CreateCondominiumCommandHandler(
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

    public async ValueTask<Result<CondominiumResult>> Handle(CreateCondominiumCommand command, CancellationToken cancellationToken)
    {
        // command.Address is only null here if CreateCondominiumCommandValidator's
        // NotNull rule somehow did not run — defensive, since the
        // ValidationBehavior pipeline step always short-circuits first in
        // production.
        if (command.Address is null)
            return Result.Failure<CondominiumResult>(AddressInvalidError);

        Address address;
        try
        {
            address = Address.Create(
                command.Address.ZipCode, command.Address.Street, command.Address.Number,
                command.Address.Complement, command.Address.Neighborhood, command.Address.City,
                command.Address.State, command.Address.Country);
        }
        catch (ArgumentException)
        {
            return Result.Failure<CondominiumResult>(AddressInvalidError);
        }

        var now = _timeProvider.GetUtcNow();
        var condominiumId = Guid.NewGuid();

        Condominium condominium;
        try
        {
            condominium = Condominium.Create(condominiumId, command.TenantId, command.Name, address, now);
        }
        catch (ArgumentException)
        {
            return Result.Failure<CondominiumResult>(new Error("condominium_name_invalid", "condominium_name_invalid"));
        }

        _repository.Add(condominium);

        var correlationId = Guid.NewGuid();

        _auditWriter.Record(PropertyAuditEntry.Create(
            Guid.NewGuid(), command.TenantId, command.ActorId, "Condominium", condominiumId,
            "condominium_created", changedFields: [], now));

        _eventCollector.Enqueue(new CondominiumCreated
        {
            TenantId = command.TenantId,
            AggregateId = condominiumId,
            AggregateType = "Condominium",
            CorrelationId = correlationId,
            ActorType = "User",
            ActorId = command.ActorId.ToString(),
            CondominiumId = condominiumId,
        });

        var result = new CondominiumResult(
            condominium.Id,
            condominium.Name,
            AddressResultMapper.ToResult(condominium.Address),
            condominium.CreatedAt,
            condominium.UpdatedAt);

        return Result.Success(result);
    }
}
