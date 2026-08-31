using FluentAssertions;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.AIAgent.Application.Tools;
using IHostPro.Contexts.AIAgent.Infrastructure.Tools;
using IHostPro.Contexts.Reservations.Application.Reservations;

namespace IHostPro.Contexts.AIAgent.Tests.Unit.Tools;

public class GetReservationSummaryToolTests
{
    private static readonly AgentToolContext Context = new(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

    [Fact]
    public async Task ExecuteAsync_maps_only_guest_safe_fields()
    {
        var dispatcher = new FakeReservationsRequestDispatcher();
        var query = new GetReservationDetailQuery(Context.ReservationId);
        var reservation = new ReservationResult(
            Context.ReservationId, Guid.NewGuid(), "Guest Name", "+5511999999999",
            new DateTimeOffset(2026, 9, 1, 14, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 9, 5, 11, 0, 0, TimeSpan.Zero),
            2, "Confirmed", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        dispatcher.Stub.SetResponse(query, Result.Success(reservation));
        var tool = new GetReservationSummaryTool(dispatcher);

        var result = await tool.ExecuteAsync(Context, null, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Content.Should().Contain("Confirmed");
        result.Content.Should().Contain(reservation.PropertyId.ToString());
        result.Content.Should().NotContain("Guest Name", "guest PII must never leave the tool result");
        result.Content.Should().NotContain("+5511999999999", "guest PII must never leave the tool result");
    }

    [Fact]
    public async Task ExecuteAsync_propagates_the_dispatcher_failure_code()
    {
        var dispatcher = new FakeReservationsRequestDispatcher();
        var query = new GetReservationDetailQuery(Context.ReservationId);
        dispatcher.Stub.SetResponse(query, Result.Failure<ReservationResult>(new Error("reservation_not_found", "reservation_not_found")));
        var tool = new GetReservationSummaryTool(dispatcher);

        var result = await tool.ExecuteAsync(Context, null, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.FailureCode.Should().Be("reservation_not_found");
    }
}
