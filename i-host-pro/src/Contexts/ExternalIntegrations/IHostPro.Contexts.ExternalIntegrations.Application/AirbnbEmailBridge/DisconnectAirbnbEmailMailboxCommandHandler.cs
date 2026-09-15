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

    public DisconnectAirbnbEmailMailboxCommandHandler(
        IAirbnbEmailMailboxConnectionRepository repository, IAirbnbEmailTokenCacheStore tokenCacheStore)
    {
        _repository = repository;
        _tokenCacheStore = tokenCacheStore;
    }

    public async ValueTask<Result<AirbnbEmailMailboxConnectionResult>> Handle(
        DisconnectAirbnbEmailMailboxCommand command, CancellationToken cancellationToken)
    {
        var connection = await _repository.GetForCurrentTenantAsync(cancellationToken);
        if (connection is null)
            return Result.Failure<AirbnbEmailMailboxConnectionResult>(NotFoundError);

        await _tokenCacheStore.ClearAsync(command.TenantId, cancellationToken);

        return Result.Success(ConnectAirbnbEmailMailboxCommandHandler.ToResult(connection));
    }
}
