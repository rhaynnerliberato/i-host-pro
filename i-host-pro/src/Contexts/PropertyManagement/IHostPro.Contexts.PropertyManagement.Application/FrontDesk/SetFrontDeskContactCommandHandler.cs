using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.PropertyManagement.Application.Condominiums;
using IHostPro.Contexts.PropertyManagement.Application.Errors;
using IHostPro.Contexts.PropertyManagement.Domain;

namespace IHostPro.Contexts.PropertyManagement.Application.FrontDesk;

/// <summary>
/// Upserts the single active front desk contact for a Condominium (Fase 10,
/// Checkpoint 4). Runs inside the transaction
/// <see cref="SetFrontDeskContactTenantAwareBehavior"/> opens for this
/// command — never calls <c>SaveChangesAsync</c> itself. Deliberately
/// publishes NO Integration Event (mandate §34 — resolution is synchronous,
/// via <c>IFrontDeskContactReader</c>, so Communication never needs a
/// <c>FrontDeskContactCreated</c>/<c>Updated</c> event; avoiding an
/// unbound-consumer event).
/// </summary>
public sealed class SetFrontDeskContactCommandHandler : ICommandHandler<SetFrontDeskContactCommand, FrontDeskContactResult>
{
    private const string CreatedActionCode = "front_desk_contact_created";
    private const string UpdatedActionCode = "front_desk_contact_updated";

    private static readonly Error CondominiumNotFoundError = new(
        PropertyManagementErrorCodes.CondominiumNotFound, PropertyManagementErrorCodes.CondominiumNotFound);

    private readonly ICondominiumReader _condominiumReader;
    private readonly IFrontDeskContactRepository _repository;
    private readonly IPropertyAuditWriter _auditWriter;
    private readonly TimeProvider _timeProvider;

    public SetFrontDeskContactCommandHandler(
        ICondominiumReader condominiumReader,
        IFrontDeskContactRepository repository,
        IPropertyAuditWriter auditWriter,
        TimeProvider timeProvider)
    {
        _condominiumReader = condominiumReader;
        _repository = repository;
        _auditWriter = auditWriter;
        _timeProvider = timeProvider;
    }

    public async ValueTask<Result<FrontDeskContactResult>> Handle(SetFrontDeskContactCommand command, CancellationToken cancellationToken)
    {
        var condominium = await _condominiumReader.GetByIdAsync(command.CondominiumId, cancellationToken);
        if (condominium is null)
            return Result.Failure<FrontDeskContactResult>(CondominiumNotFoundError);

        var now = _timeProvider.GetUtcNow();
        var normalizedName = command.DisplayName.Trim();
        var normalizedPhone = command.PhoneNumber.Trim();

        var existing = await _repository.GetByCondominiumIdAsync(command.CondominiumId, cancellationToken);

        FrontDeskContact contact;
        if (existing is null)
        {
            contact = FrontDeskContact.Create(
                Guid.NewGuid(), command.TenantId, command.CondominiumId, normalizedName, normalizedPhone, command.IsActive, now);
            _repository.Add(contact);

            _auditWriter.Record(PropertyAuditEntry.Create(
                Guid.NewGuid(), command.TenantId, command.ActorId, "FrontDeskContact", contact.Id,
                CreatedActionCode, changedFields: ["display_name", "phone_number", "is_active"], now));

            return Result.Success(ToResult(contact));
        }

        contact = existing;
        var changedFields = new List<string>();

        if (!string.Equals(normalizedName, contact.DisplayName, StringComparison.Ordinal))
            changedFields.Add("display_name");
        if (!string.Equals(normalizedPhone, contact.PhoneNumber, StringComparison.Ordinal))
            changedFields.Add("phone_number");
        if (command.IsActive != contact.IsActive)
            changedFields.Add("is_active");

        if (changedFields.Count > 0)
        {
            contact.UpdateContact(normalizedName, normalizedPhone, command.IsActive, now);
            _repository.Update(contact);

            _auditWriter.Record(PropertyAuditEntry.Create(
                Guid.NewGuid(), command.TenantId, command.ActorId, "FrontDeskContact", contact.Id,
                UpdatedActionCode, changedFields, now));
        }

        return Result.Success(ToResult(contact));
    }

    private static FrontDeskContactResult ToResult(FrontDeskContact contact) => new(
        contact.Id, contact.CondominiumId, contact.DisplayName, contact.PhoneNumber,
        contact.IsActive, contact.CreatedAtUtc, contact.UpdatedAtUtc);
}
