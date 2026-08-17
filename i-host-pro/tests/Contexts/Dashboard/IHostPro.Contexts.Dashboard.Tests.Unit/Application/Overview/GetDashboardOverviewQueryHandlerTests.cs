using FluentAssertions;
using IHostPro.Contexts.Dashboard.Application.Overview;

namespace IHostPro.Contexts.Dashboard.Tests.Unit.Application.Overview;

public class GetDashboardOverviewQueryHandlerTests
{
    [Fact]
    public async Task Handle_forwards_the_query_interval_and_the_TimeProviders_now_to_the_reader_and_returns_success()
    {
        var fixedNow = new DateTimeOffset(2026, 3, 15, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new FixedTimeProvider(fixedNow);
        var from = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(2026, 3, 31, 0, 0, 0, TimeSpan.Zero);
        var reader = new RecordingDashboardOverviewReader();
        var handler = new GetDashboardOverviewQueryHandler(reader, timeProvider);

        var result = await handler.Handle(new GetDashboardOverviewQuery(from, to), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        reader.ReceivedFrom.Should().Be(from);
        reader.ReceivedTo.Should().Be(to);
        reader.ReceivedNowUtc.Should().Be(fixedNow);
    }

    /// <summary>Never resolves "now" internally — GeneratedAtUtc must trace back exclusively to the injected TimeProvider (mandate §45).</summary>
    [Fact]
    public async Task Handle_never_resolves_now_internally_it_always_comes_from_the_reader_supplied_value()
    {
        var fixedNow = new DateTimeOffset(2030, 6, 1, 8, 30, 0, TimeSpan.Zero);
        var timeProvider = new FixedTimeProvider(fixedNow);
        var reader = new RecordingDashboardOverviewReader();
        var handler = new GetDashboardOverviewQueryHandler(reader, timeProvider);

        var result = await handler.Handle(
            new GetDashboardOverviewQuery(fixedNow.AddDays(-1), fixedNow), CancellationToken.None);

        result.Value.GeneratedAtUtc.Should().Be(fixedNow);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class RecordingDashboardOverviewReader : IDashboardOverviewReader
    {
        public DateTimeOffset ReceivedFrom { get; private set; }
        public DateTimeOffset ReceivedTo { get; private set; }
        public DateTimeOffset ReceivedNowUtc { get; private set; }

        public Task<DashboardOverviewResult> GetOverviewAsync(
            DateTimeOffset from, DateTimeOffset to, DateTimeOffset nowUtc, CancellationToken cancellationToken)
        {
            ReceivedFrom = from;
            ReceivedTo = to;
            ReceivedNowUtc = nowUtc;

            return Task.FromResult(new DashboardOverviewResult(
                new DashboardPeriodResult(from, to),
                new DashboardReservationsOverviewResult(0, 0, 0, 0, []),
                new DashboardHousekeepingOverviewResult(0, 0, 0, 0, 0, 0, 0, 0),
                new DashboardPropertiesOverviewResult(0, 0, 0),
                new DashboardOccurrencesOverviewResult(0, []),
                nowUtc));
        }
    }
}
