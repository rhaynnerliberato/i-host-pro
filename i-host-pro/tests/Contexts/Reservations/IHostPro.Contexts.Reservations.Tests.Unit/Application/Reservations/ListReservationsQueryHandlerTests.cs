using FluentAssertions;
using IHostPro.Contexts.Reservations.Application.Reservations;

namespace IHostPro.Contexts.Reservations.Tests.Unit.Application.Reservations;

public class ListReservationsQueryHandlerTests
{
    [Fact]
    public async Task Returns_the_page_the_reader_produces()
    {
        var summary = new ReservationSummaryResult(
            Guid.NewGuid(), Guid.NewGuid(), "Guest", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1), 2,
            "confirmed", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var handler = new ListReservationsQueryHandler(FakeReservationReader.WithSummaries([summary]));

        var result = await handler.Handle(new(null, null, null, null, null, null), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().ContainSingle();
        result.Value.Page.Should().Be(ListReservationsQueryHandler.DefaultPage);
        result.Value.PageSize.Should().Be(ListReservationsQueryHandler.DefaultPageSize);
    }

    [Fact]
    public async Task Supplied_page_and_page_size_are_used_instead_of_defaults()
    {
        var handler = new ListReservationsQueryHandler(FakeReservationReader.WithSummaries([]));

        var result = await handler.Handle(new(null, null, null, null, 3, 10), CancellationToken.None);

        result.Value.Page.Should().Be(3);
        result.Value.PageSize.Should().Be(10);
    }
}
