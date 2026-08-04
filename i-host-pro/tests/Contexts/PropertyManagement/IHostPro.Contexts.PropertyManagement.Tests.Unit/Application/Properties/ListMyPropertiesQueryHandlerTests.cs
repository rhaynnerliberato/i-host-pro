using FluentAssertions;
using IHostPro.BuildingBlocks.Application;
using IHostPro.Contexts.PropertyManagement.Application.Properties;

namespace IHostPro.Contexts.PropertyManagement.Tests.Unit.Application.Properties;

public class ListMyPropertiesQueryHandlerTests
{
    [Fact]
    public async Task Defaults_to_page_1_and_page_size_20_when_not_supplied()
    {
        var ownerUserId = Guid.NewGuid();
        var reader = FakePropertyReader.WithList(new PagedResult<PropertySummaryResult>(1, 20, 0, []));
        var handler = new ListMyPropertiesQueryHandler(reader);

        await handler.Handle(new ListMyPropertiesQuery(ownerUserId, null, null), CancellationToken.None);

        reader.LastRequestedOwnerUserId.Should().Be(ownerUserId);
        reader.LastRequestedPage.Should().Be(1);
        reader.LastRequestedPageSize.Should().Be(20);
    }

    [Fact]
    public async Task Forwards_the_supplied_page_and_page_size()
    {
        var ownerUserId = Guid.NewGuid();
        var reader = FakePropertyReader.WithList(new PagedResult<PropertySummaryResult>(3, 50, 0, []));
        var handler = new ListMyPropertiesQueryHandler(reader);

        await handler.Handle(new ListMyPropertiesQuery(ownerUserId, 3, 50), CancellationToken.None);

        reader.LastRequestedPage.Should().Be(3);
        reader.LastRequestedPageSize.Should().Be(50);
    }

    [Fact]
    public async Task Returns_the_readers_result_as_is_regardless_of_property_status()
    {
        var ownerUserId = Guid.NewGuid();
        var expected = new PagedResult<PropertySummaryResult>(
            1, 20, 1, [new PropertySummaryResult(Guid.NewGuid(), "A1", "Studio A1", 2, null, "archived", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)]);
        var reader = FakePropertyReader.WithList(expected);
        var handler = new ListMyPropertiesQueryHandler(reader);

        var result = await handler.Handle(new ListMyPropertiesQuery(ownerUserId, null, null), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeSameAs(expected);
    }

    [Fact]
    public async Task Cancellation_token_is_accepted_without_throwing()
    {
        var reader = FakePropertyReader.WithList(new PagedResult<PropertySummaryResult>(1, 20, 0, []));
        var handler = new ListMyPropertiesQueryHandler(reader);
        using var cts = new CancellationTokenSource();

        var act = async () => await handler.Handle(new ListMyPropertiesQuery(Guid.NewGuid(), null, null), cts.Token);

        await act.Should().NotThrowAsync();
    }
}
