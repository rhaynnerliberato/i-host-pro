using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;

namespace IHostPro.Contexts.Housekeeping.Application.Cleanings;

/// <inheritdoc cref="ListCleaningsQuery"/>
public sealed class ListCleaningsQueryHandler : IQueryHandler<ListCleaningsQuery, PagedResult<CleaningSummaryResult>>
{
    public const int DefaultPage = 1;
    public const int DefaultPageSize = 20;

    private readonly ICleaningReader _reader;

    public ListCleaningsQueryHandler(ICleaningReader reader) => _reader = reader;

    public async ValueTask<Result<PagedResult<CleaningSummaryResult>>> Handle(
        ListCleaningsQuery query, CancellationToken cancellationToken)
    {
        var page = query.Page ?? DefaultPage;
        var pageSize = query.PageSize ?? DefaultPageSize;

        var result = await _reader.ListAsync(
            query.Status, query.PropertyId, query.AssignedHousekeeperUserId, page, pageSize, cancellationToken);

        return Result.Success(result);
    }
}
