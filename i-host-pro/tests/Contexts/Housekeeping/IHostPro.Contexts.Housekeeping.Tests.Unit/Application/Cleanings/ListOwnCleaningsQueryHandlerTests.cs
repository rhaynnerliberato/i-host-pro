using FluentAssertions;
using IHostPro.Contexts.Housekeeping.Application.Cleanings;

namespace IHostPro.Contexts.Housekeeping.Tests.Unit.Application.Cleanings;

public class ListOwnCleaningsQueryHandlerTests
{
    [Fact]
    public async Task Returns_the_page_the_reader_produces_and_forwards_the_callers_own_housekeeper_id()
    {
        var housekeeperUserId = Guid.NewGuid();
        var summary = new CleaningSummaryResult(
            Guid.NewGuid(), Guid.NewGuid(), null, housekeeperUserId, "Assigned", DateTimeOffset.UtcNow, null);
        var reader = FakeCleaningReader.WithSummaries([summary]);
        var handler = new ListOwnCleaningsQueryHandler(reader);

        var result = await handler.Handle(new(housekeeperUserId, null, null, null), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().ContainSingle();
        result.Value.Page.Should().Be(ListCleaningsQueryHandler.DefaultPage);
        result.Value.PageSize.Should().Be(ListCleaningsQueryHandler.DefaultPageSize);
        reader.LastHousekeeperUserId.Should().Be(housekeeperUserId);
    }

    [Fact]
    public async Task Forwards_explicit_page_status_and_page_size()
    {
        var housekeeperUserId = Guid.NewGuid();
        var reader = FakeCleaningReader.WithSummaries([]);
        var handler = new ListOwnCleaningsQueryHandler(reader);

        await handler.Handle(new(housekeeperUserId, "Started", 3, 10), CancellationToken.None);

        reader.LastHousekeeperUserId.Should().Be(housekeeperUserId);
        reader.LastStatus.Should().Be("Started");
        reader.LastPage.Should().Be(3);
        reader.LastPageSize.Should().Be(10);
    }
}
