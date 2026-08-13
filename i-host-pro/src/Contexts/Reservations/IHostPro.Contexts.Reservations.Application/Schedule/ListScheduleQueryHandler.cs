using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;

namespace IHostPro.Contexts.Reservations.Application.Schedule;

/// <inheritdoc cref="ListScheduleQuery"/>
public sealed class ListScheduleQueryHandler : IQueryHandler<ListScheduleQuery, IReadOnlyList<ScheduleItemResult>>
{
    private readonly IScheduleReader _reader;

    public ListScheduleQueryHandler(IScheduleReader reader) => _reader = reader;

    public async ValueTask<Result<IReadOnlyList<ScheduleItemResult>>> Handle(
        ListScheduleQuery query, CancellationToken cancellationToken)
    {
        var items = await _reader.ListAsync(
            query.From, query.To, query.PropertyId, query.HousekeeperUserId, query.EventType, cancellationToken);

        return Result.Success(items);
    }
}
