using FluentAssertions;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.AIAgent.Application.Tools;
using IHostPro.Contexts.AIAgent.Infrastructure.Tools;
using IHostPro.Contexts.Housekeeping.Application.Cleanings;

namespace IHostPro.Contexts.AIAgent.Tests.Unit.Tools;

public class GetCleaningStatusToolTests
{
    private static readonly AgentToolContext Context = new(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

    [Fact]
    public async Task ExecuteAsync_reports_the_status_and_available_timestamps()
    {
        var dispatcher = new FakeHousekeepingRequestDispatcher();
        var status = new CleaningStatusResult("Started", DateTimeOffset.UtcNow.AddHours(-1), null);
        dispatcher.Stub.SetResponse(new GetCleaningStatusByReservationQuery(Context.ReservationId), Result.Success(status));
        var tool = new GetCleaningStatusTool(dispatcher);

        var result = await tool.ExecuteAsync(Context, null, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Content.Should().Contain("Started");
        result.Content.Should().NotContain("Concluída", "no CompletedAtUtc was supplied — never invent a completion fact");
    }

    [Fact]
    public async Task ExecuteAsync_propagates_the_not_found_failure()
    {
        var dispatcher = new FakeHousekeepingRequestDispatcher();
        dispatcher.Stub.SetResponse(
            new GetCleaningStatusByReservationQuery(Context.ReservationId), Result.Failure<CleaningStatusResult>(new Error("cleaning_not_found", "cleaning_not_found")));
        var tool = new GetCleaningStatusTool(dispatcher);

        var result = await tool.ExecuteAsync(Context, null, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.FailureCode.Should().Be("cleaning_not_found");
    }
}
