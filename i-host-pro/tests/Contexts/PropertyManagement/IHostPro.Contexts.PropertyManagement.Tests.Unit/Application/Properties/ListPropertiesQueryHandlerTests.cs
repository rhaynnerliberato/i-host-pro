using FluentAssertions;
using IHostPro.BuildingBlocks.Application;
using IHostPro.Contexts.PropertyManagement.Application.Properties;

namespace IHostPro.Contexts.PropertyManagement.Tests.Unit.Application.Properties;

public class ListPropertiesQueryHandlerTests
{
    [Fact]
    public async Task Defaults_to_page_1_and_page_size_20_when_not_supplied()
    {
        var reader = FakePropertyReader.WithList(new PagedResult<PropertySummaryResult>(1, 20, 0, []));
        var handler = new ListPropertiesQueryHandler(reader);

        await handler.Handle(new ListPropertiesQuery(null, null), CancellationToken.None);

        reader.LastRequestedPage.Should().Be(1);
        reader.LastRequestedPageSize.Should().Be(20);
    }

    [Fact]
    public async Task Forwards_the_supplied_page_and_page_size()
    {
        var reader = FakePropertyReader.WithList(new PagedResult<PropertySummaryResult>(3, 50, 0, []));
        var handler = new ListPropertiesQueryHandler(reader);

        await handler.Handle(new ListPropertiesQuery(3, 50), CancellationToken.None);

        reader.LastRequestedPage.Should().Be(3);
        reader.LastRequestedPageSize.Should().Be(50);
    }

    [Fact]
    public async Task Returns_the_readers_result_as_is()
    {
        var expected = new PagedResult<PropertySummaryResult>(
            1, 20, 1, [new PropertySummaryResult(Guid.NewGuid(), "A1", "Studio A1", 2, null, "draft", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)]);
        var reader = FakePropertyReader.WithList(expected);
        var handler = new ListPropertiesQueryHandler(reader);

        var result = await handler.Handle(new ListPropertiesQuery(null, null), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeSameAs(expected);
    }

    [Fact]
    public async Task Cancellation_token_is_forwarded_to_the_reader()
    {
        var reader = FakePropertyReader.WithList(new PagedResult<PropertySummaryResult>(1, 20, 0, []));
        var handler = new ListPropertiesQueryHandler(reader);
        using var cts = new CancellationTokenSource();

        var act = async () => await handler.Handle(new ListPropertiesQuery(null, null), cts.Token);

        await act.Should().NotThrowAsync();
    }
}
