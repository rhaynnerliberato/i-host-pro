using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.Communication.Domain;

namespace IHostPro.Contexts.Communication.Application;

/// <summary>Handles <see cref="GetAdministratorNotificationContactQuery"/> (Fase 11, Checkpoint 6).</summary>
public sealed class GetAdministratorNotificationContactQueryHandler
    : IQueryHandler<GetAdministratorNotificationContactQuery, AdministratorNotificationContactResult?>
{
    private readonly IAdministratorNotificationContactRepository _repository;
    private readonly ICommunicationTransactionExecutor _transactionExecutor;

    public GetAdministratorNotificationContactQueryHandler(
        IAdministratorNotificationContactRepository repository, ICommunicationTransactionExecutor transactionExecutor)
    {
        _repository = repository;
        _transactionExecutor = transactionExecutor;
    }

    public ValueTask<Result<AdministratorNotificationContactResult?>> Handle(
        GetAdministratorNotificationContactQuery query, CancellationToken cancellationToken) =>
        new(_transactionExecutor.ExecuteAsync(async () =>
        {
            var contact = await _repository.GetActiveByTenantIdAsync(query.TenantId, cancellationToken);
            return Result.Success<AdministratorNotificationContactResult?>(contact is null ? null : ToResult(contact));
        }, cancellationToken));

    private static AdministratorNotificationContactResult ToResult(AdministratorNotificationContact contact) => new(
        contact.Id, contact.TenantId, contact.DestinationPhone, contact.IsActive, contact.CreatedAtUtc, contact.UpdatedAtUtc);
}
