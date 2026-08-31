using FluentAssertions;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.AIAgent.Application.Tools;
using IHostPro.Contexts.AIAgent.Infrastructure.Tools;
using IHostPro.Contexts.Reservations.Application.Reservations;
using IHostPro.Contexts.Reservations.Application.Schedule;

namespace IHostPro.Contexts.AIAgent.Tests.Unit.Tools;

public class GetAvailabilityToolTests
{
    private static readonly AgentToolContext Context = new(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
    private static readonly Guid PropertyId = Guid.NewGuid();

    private static ReservationResult BuildReservation() => new(
        Context.ReservationId, PropertyId, "Guest", null,
        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(3), 2, "Confirmed", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    [Fact]
    public async Task ExecuteAsync_reports_free_when_no_schedule_items_conflict()
    {
        var dispatcher = new FakeReservationsRequestDispatcher();
        dispatcher.Stub.SetResponse(new GetReservationDetailQuery(Context.ReservationId), Result.Success(BuildReservation()));
        dispatcher.Stub.SetResponse(
            new ListScheduleQuery(default, default, PropertyId, null, null),
            Result.Success<IReadOnlyList<ScheduleItemResult>>([]));
        var tool = new GetAvailabilityTool(dispatcher, TimeProvider.System);

        var result = await tool.ExecuteAsync(Context, null, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Content.Should().Contain("livre");
    }

    [Fact]
    public async Task ExecuteAsync_reports_a_conflict_count_never_an_eligibility_conclusion()
    {
        var dispatcher = new FakeReservationsRequestDispatcher();
        dispatcher.Stub.SetResponse(new GetReservationDetailQuery(Context.ReservationId), Result.Success(BuildReservation()));
        var items = new List<ScheduleItemResult>
        {
            new(Guid.NewGuid(), "Reservation", PropertyId, DateTimeOffset.UtcNow.AddDays(1), null, "Confirmed", null, Guid.NewGuid()),
        };
        dispatcher.Stub.SetResponse(
            new ListScheduleQuery(default, default, PropertyId, null, null), Result.Success<IReadOnlyList<ScheduleItemResult>>(items));
        var tool = new GetAvailabilityTool(dispatcher, TimeProvider.System);

        var result = await tool.ExecuteAsync(Context, null, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Content.Should().Contain("1 evento");
        result.Content.Should().NotContain("aprovad", "this tool never resolves an eligibility/approval conclusion");
        result.Content.Should().NotContain("elegí");
    }
}
