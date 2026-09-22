using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;

namespace IHostPro.Contexts.ExternalIntegrations.Application.AirbnbEmailBridge;

/// <inheritdoc cref="ListAirbnbEmailMessageReceiptsQuery"/>
public sealed class ListAirbnbEmailMessageReceiptsQueryHandler
    : IQueryHandler<ListAirbnbEmailMessageReceiptsQuery, PagedResult<AirbnbEmailMessageReceiptResult>>
{
    public const int DefaultPage = 1;
    public const int DefaultPageSize = 20;

    private readonly IAirbnbEmailMessageReceiptRepository _repository;

    public ListAirbnbEmailMessageReceiptsQueryHandler(IAirbnbEmailMessageReceiptRepository repository) => _repository = repository;

    public async ValueTask<Result<PagedResult<AirbnbEmailMessageReceiptResult>>> Handle(
        ListAirbnbEmailMessageReceiptsQuery query, CancellationToken cancellationToken)
    {
        var page = query.Page ?? DefaultPage;
        var pageSize = query.PageSize ?? DefaultPageSize;

        var result = await _repository.ListForCurrentTenantAsync(query.Status, query.ReasonCode, page, pageSize, cancellationToken);

        return Result.Success(result);
    }
}
