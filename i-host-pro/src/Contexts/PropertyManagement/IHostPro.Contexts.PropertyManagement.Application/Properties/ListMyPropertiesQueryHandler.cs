using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;

namespace IHostPro.Contexts.PropertyManagement.Application.Properties;

public sealed class ListMyPropertiesQueryHandler : IQueryHandler<ListMyPropertiesQuery, PagedResult<PropertySummaryResult>>
{
    public const int DefaultPageSize = 20;

    private readonly IPropertyReader _reader;

    public ListMyPropertiesQueryHandler(IPropertyReader reader) => _reader = reader;

    public async ValueTask<Result<PagedResult<PropertySummaryResult>>> Handle(
        ListMyPropertiesQuery query, CancellationToken cancellationToken)
    {
        var page = query.Page ?? 1;
        var pageSize = query.PageSize ?? DefaultPageSize;

        var result = await _reader.ListMineAsync(query.OwnerUserId, page, pageSize, cancellationToken);

        return Result.Success(result);
    }
}
