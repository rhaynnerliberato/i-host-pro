using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.Communication.Domain;

namespace IHostPro.Contexts.Communication.Application;

/// <summary>
/// Handles <see cref="UpsertAdministratorNotificationContactCommand"/> (Fase
/// 11, Checkpoint 6) — creates the Tenant's first contact, or replaces the
/// destination phone of the existing ACTIVE one; never creates a second row
/// while one is active (the partial unique index backstops this at the
/// database level too).
/// </summary>
public sealed class UpsertAdministratorNotificationContactCommandHandler
    : ICommandHandler<UpsertAdministratorNotificationContactCommand, AdministratorNotificationContactResult>
{
    private readonly IAdministratorNotificationContactRepository _repository;
    private readonly ICommunicationTransactionExecutor _transactionExecutor;
    private readonly TimeProvider _timeProvider;

    public UpsertAdministratorNotificationContactCommandHandler(
        IAdministratorNotificationContactRepository repository, ICommunicationTransactionExecutor transactionExecutor, TimeProvider timeProvider)
    {
        _repository = repository;
        _transactionExecutor = transactionExecutor;
        _timeProvider = timeProvider;
    }

    public ValueTask<Result<AdministratorNotificationContactResult>> Handle(
        UpsertAdministratorNotificationContactCommand command, CancellationToken cancellationToken) =>
        new(_transactionExecutor.ExecuteAsync(async () =>
        {
            var now = _timeProvider.GetUtcNow();
            var existing = await _repository.GetActiveByTenantIdAsync(command.TenantId, cancellationToken);

            if (existing is not null)
            {
                existing.ChangeDestinationPhone(command.DestinationPhone, now);
                _repository.Update(existing);
                return Result.Success(ToResult(existing));
            }

            var contact = AdministratorNotificationContact.Create(Guid.NewGuid(), command.TenantId, command.DestinationPhone, now);
            _repository.Add(contact);
            return Result.Success(ToResult(contact));
        }, cancellationToken));

    private static AdministratorNotificationContactResult ToResult(AdministratorNotificationContact contact) => new(
        contact.Id, contact.TenantId, contact.DestinationPhone, contact.IsActive, contact.CreatedAtUtc, contact.UpdatedAtUtc);
}
