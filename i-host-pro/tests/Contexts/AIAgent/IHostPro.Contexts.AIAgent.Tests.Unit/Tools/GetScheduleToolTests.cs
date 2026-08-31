using FluentAssertions;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.AIAgent.Application.Tools;
using IHostPro.Contexts.AIAgent.Infrastructure.Tools;
using IHostPro.Contexts.Reservations.Application.Reservations;
using IHostPro.Contexts.Reservations.Application.Schedule;

namespace IHostPro.Contexts.AIAgent.Tests.Unit.Tools;

public class GetScheduleToolTests
{
    private static readonly AgentToolContext Context = new(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
    private static readonly Guid PropertyId = Guid.NewGuid();

    private static ReservationResult BuildReservation() => new(
        Context.ReservationId, PropertyId, "Guest", null,
        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(3), 2, "Confirmed", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    [Fact]
    public async Task ExecuteAsync_resolves_PropertyId_from_the_reservation_and_lists_the_schedule()
    {
        var dispatcher = new FakeReservationsRequestDispatcher();
        dispatcher.Stub.SetResponse(new GetReservationDetailQuery(Context.ReservationId), Result.Success(BuildReservation()));
        var items = new List<ScheduleItemResult>
        {
            new(Guid.NewGuid(), "Cleaning", PropertyId, DateTimeOffset.UtcNow.AddDays(1), null, "Pending", null, Guid.NewGuid()),
        };
        // Any ListScheduleQuery instance is matched by type alone in the stub, PropertyId is asserted separately below.
        var tool = new GetScheduleTool(dispatcher, TimeProvider.System);
        dispatcher.Stub.SetResponse(
            new ListScheduleQuery(default, default, PropertyId, null, null), Result.Success<IReadOnlyList<ScheduleItemResult>>(items));

        var result = await tool.ExecuteAsync(Context, null, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Content.Should().Contain("Cleaning");
        var scheduleRequest = dispatcher.Stub.ReceivedRequests.OfType<ListScheduleQuery>().Single();
        scheduleRequest.PropertyId.Should().Be(PropertyId);
    }

    [Fact]
    public async Task ExecuteAsync_clamps_the_days_argument_to_the_maximum_window()
    {
        var dispatcher = new FakeReservationsRequestDispatcher();
        dispatcher.Stub.SetResponse(new GetReservationDetailQuery(Context.ReservationId), Result.Success(BuildReservation()));
        dispatcher.Stub.SetResponse(
            new ListScheduleQuery(default, default, PropertyId, null, null),
            Result.Success<IReadOnlyList<ScheduleItemResult>>([]));
        var tool = new GetScheduleTool(dispatcher, TimeProvider.System);

        var result = await tool.ExecuteAsync(Context, new Dictionary<string, string> { ["days"] = "9999" }, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var scheduleRequest = dispatcher.Stub.ReceivedRequests.OfType<ListScheduleQuery>().Single();
        (scheduleRequest.To - scheduleRequest.From).TotalDays.Should().BeApproximately(GetScheduleTool.MaxDays, 0.01);
    }

    [Fact]
    public async Task ExecuteAsync_propagates_the_reservation_lookup_failure()
    {
        var dispatcher = new FakeReservationsRequestDispatcher();
        dispatcher.Stub.SetResponse(
            new GetReservationDetailQuery(Context.ReservationId), Result.Failure<ReservationResult>(new Error("reservation_not_found", "reservation_not_found")));
        var tool = new GetScheduleTool(dispatcher, TimeProvider.System);

        var result = await tool.ExecuteAsync(Context, null, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.FailureCode.Should().Be("reservation_not_found");
    }
}
