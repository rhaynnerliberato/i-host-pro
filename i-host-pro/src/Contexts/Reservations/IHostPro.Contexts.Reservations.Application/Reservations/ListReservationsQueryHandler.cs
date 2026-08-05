using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;

namespace IHostPro.Contexts.Reservations.Application.Reservations;

/// <inheritdoc cref="ListReservationsQuery"/>
public sealed class ListReservationsQueryHandler : IQueryHandler<ListReservationsQuery, PagedResult<ReservationSummaryResult>>
{
    public const int DefaultPage = 1;
    public const int DefaultPageSize = 20;

    private readonly IReservationReader _reader;

    public ListReservationsQueryHandler(IReservationReader reader) => _reader = reader;

    public async ValueTask<Result<PagedResult<ReservationSummaryResult>>> Handle(
        ListReservationsQuery query, CancellationToken cancellationToken)
    {
        var page = query.Page ?? DefaultPage;
        var pageSize = query.PageSize ?? DefaultPageSize;

        var result = await _reader.ListAsync(
            query.PropertyId, query.Status, query.From, query.To, page, pageSize, cancellationToken);

        return Result.Success(result);
    }
}
