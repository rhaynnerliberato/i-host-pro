using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;

namespace IHostPro.Contexts.Dashboard.Application.Overview;

/// <inheritdoc cref="GetDashboardOverviewQuery"/>
public sealed class GetDashboardOverviewQueryHandler : IQueryHandler<GetDashboardOverviewQuery, DashboardOverviewResult>
{
    private readonly IDashboardOverviewReader _reader;
    private readonly TimeProvider _timeProvider;

    public GetDashboardOverviewQueryHandler(IDashboardOverviewReader reader, TimeProvider timeProvider)
    {
        _reader = reader;
        _timeProvider = timeProvider;
    }

    public async ValueTask<Result<DashboardOverviewResult>> Handle(
        GetDashboardOverviewQuery query, CancellationToken cancellationToken)
    {
        var nowUtc = _timeProvider.GetUtcNow();
        var overview = await _reader.GetOverviewAsync(query.From, query.To, nowUtc, cancellationToken);
        return Result.Success(overview);
    }
}
