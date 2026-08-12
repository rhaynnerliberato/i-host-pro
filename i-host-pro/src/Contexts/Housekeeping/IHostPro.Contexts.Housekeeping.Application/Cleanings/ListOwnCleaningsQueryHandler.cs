using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;

namespace IHostPro.Contexts.Housekeeping.Application.Cleanings;

/// <inheritdoc cref="ListOwnCleaningsQuery"/>
public sealed class ListOwnCleaningsQueryHandler : IQueryHandler<ListOwnCleaningsQuery, PagedResult<CleaningSummaryResult>>
{
    private readonly ICleaningReader _reader;

    public ListOwnCleaningsQueryHandler(ICleaningReader reader) => _reader = reader;

    public async ValueTask<Result<PagedResult<CleaningSummaryResult>>> Handle(
        ListOwnCleaningsQuery query, CancellationToken cancellationToken)
    {
        var page = query.Page ?? ListCleaningsQueryHandler.DefaultPage;
        var pageSize = query.PageSize ?? ListCleaningsQueryHandler.DefaultPageSize;

        var result = await _reader.ListForHousekeeperAsync(
            query.HousekeeperUserId, query.Status, page, pageSize, cancellationToken);

        return Result.Success(result);
    }
}
