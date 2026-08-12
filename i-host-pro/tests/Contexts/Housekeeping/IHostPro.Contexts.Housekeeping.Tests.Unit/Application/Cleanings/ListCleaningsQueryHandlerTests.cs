using FluentAssertions;
using IHostPro.Contexts.Housekeeping.Application.Cleanings;

namespace IHostPro.Contexts.Housekeeping.Tests.Unit.Application.Cleanings;

public class ListCleaningsQueryHandlerTests
{
    [Fact]
    public async Task Returns_the_page_the_reader_produces()
    {
        var summary = new CleaningSummaryResult(
            Guid.NewGuid(), Guid.NewGuid(), null, null, "Pending", DateTimeOffset.UtcNow, null);
        var handler = new ListCleaningsQueryHandler(FakeCleaningReader.WithSummaries([summary]));

        var result = await handler.Handle(new(null, null, null, null, null), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().ContainSingle();
        result.Value.Page.Should().Be(ListCleaningsQueryHandler.DefaultPage);
        result.Value.PageSize.Should().Be(ListCleaningsQueryHandler.DefaultPageSize);
    }

    [Fact]
    public async Task Supplied_page_and_page_size_are_used_instead_of_defaults()
    {
        var handler = new ListCleaningsQueryHandler(FakeCleaningReader.WithSummaries([]));

        var result = await handler.Handle(new(null, null, null, 3, 10), CancellationToken.None);

        result.Value.Page.Should().Be(3);
        result.Value.PageSize.Should().Be(10);
    }

    [Fact]
    public async Task Status_propertyId_and_assignedHousekeeperUserId_filters_are_forwarded_to_the_reader_unchanged()
    {
        var reader = FakeCleaningReader.WithSummaries([]);
        var handler = new ListCleaningsQueryHandler(reader);
        var propertyId = Guid.NewGuid();
        var housekeeperId = Guid.NewGuid();

        await handler.Handle(new("Started", propertyId, housekeeperId, null, null), CancellationToken.None);

        reader.LastStatus.Should().Be("Started");
        reader.LastPropertyId.Should().Be(propertyId);
        reader.LastAssignedHousekeeperUserId.Should().Be(housekeeperId);
    }
}
