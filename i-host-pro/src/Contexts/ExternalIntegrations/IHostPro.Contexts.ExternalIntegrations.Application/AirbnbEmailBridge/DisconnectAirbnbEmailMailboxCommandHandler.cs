using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;

namespace IHostPro.Contexts.ExternalIntegrations.Application.AirbnbEmailBridge;

/// <summary>
/// Reuses <see cref="IAirbnbEmailTokenCacheStore.ClearAsync"/> for the actual
/// disconnect (it already implements exactly this: disable + clear cache,
/// never delete) rather than duplicating that logic against the repository
/// directly.
/// </summary>
public sealed class DisconnectAirbnbEmailMailboxCommandHandler
    : ICommandHandler<DisconnectAirbnbEmailMailboxCommand, AirbnbEmailMailboxConnectionResult>
{
    private static readonly Error NotFoundError = new(AirbnbEmailBridgeErrorCodes.ConnectionNotFound, AirbnbEmailBridgeErrorCodes.ConnectionNotFound);

    private readonly IAirbnbEmailMailboxConnectionRepository _repository;
    private readonly IAirbnbEmailTokenCacheStore _tokenCacheStore;
    private readonly IAirbnbEmailUnitOfWork _unitOfWork;

    public DisconnectAirbnbEmailMailboxCommandHandler(
        IAirbnbEmailMailboxConnectionRepository repository, IAirbnbEmailTokenCacheStore tokenCacheStore, IAirbnbEmailUnitOfWork unitOfWork)
    {
        _repository = repository;
        _tokenCacheStore = tokenCacheStore;
        _unitOfWork = unitOfWork;
    }

    public async ValueTask<Result<AirbnbEmailMailboxConnectionResult>> Handle(
        DisconnectAirbnbEmailMailboxCommand command, CancellationToken cancellationToken)
    {
        // Its own tenant-scoped transaction (no ambient one wraps this
        // command - see the DI registration's own remarks): without it, RLS
        // would hide a genuinely existing connection row.
        var connection = await _unitOfWork.ExecuteAsync(
            () => _repository.GetForCurrentTenantAsync(cancellationToken), cancellationToken);
        if (connection is null)
            return Result.Failure<AirbnbEmailMailboxConnectionResult>(NotFoundError);

        await _tokenCacheStore.ClearAsync(command.TenantId, cancellationToken);

        return Result.Success(ConnectAirbnbEmailMailboxCommandHandler.ToResult(connection));
    }
}
