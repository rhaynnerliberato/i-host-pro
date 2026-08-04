using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;

namespace IHostPro.Contexts.PropertyManagement.Application.Properties;

public sealed class ListPropertiesQueryHandler : IQueryHandler<ListPropertiesQuery, PagedResult<PropertySummaryResult>>
{
    public const int DefaultPageSize = 20;

    private readonly IPropertyReader _reader;

    public ListPropertiesQueryHandler(IPropertyReader reader) => _reader = reader;

    public async ValueTask<Result<PagedResult<PropertySummaryResult>>> Handle(
        ListPropertiesQuery query, CancellationToken cancellationToken)
    {
        var page = query.Page ?? 1;
        var pageSize = query.PageSize ?? DefaultPageSize;

        var result = await _reader.ListAsync(page, pageSize, cancellationToken);

        return Result.Success(result);
    }
}
