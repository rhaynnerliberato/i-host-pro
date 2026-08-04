using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;

namespace IHostPro.Contexts.PropertyManagement.Application.Condominiums;

public sealed class ListCondominiumsQueryHandler : IQueryHandler<ListCondominiumsQuery, PagedResult<CondominiumSummaryResult>>
{
    public const int DefaultPageSize = 20;

    private readonly ICondominiumReader _reader;

    public ListCondominiumsQueryHandler(ICondominiumReader reader) => _reader = reader;

    public async ValueTask<Result<PagedResult<CondominiumSummaryResult>>> Handle(
        ListCondominiumsQuery query, CancellationToken cancellationToken)
    {
        var page = query.Page ?? 1;
        var pageSize = query.PageSize ?? DefaultPageSize;

        var result = await _reader.ListAsync(page, pageSize, cancellationToken);

        return Result.Success(result);
    }
}
