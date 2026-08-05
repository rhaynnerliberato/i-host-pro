using FluentAssertions;
using IHostPro.Contexts.Reservations.Application.Errors;
using IHostPro.Contexts.Reservations.Application.Reservations;

namespace IHostPro.Contexts.Reservations.Tests.Unit.Application.Reservations;

public class GetReservationDetailQueryHandlerTests
{
    [Fact]
    public async Task An_existing_reservation_returns_its_detail()
    {
        var reservationId = Guid.NewGuid();
        var detail = new ReservationResult(
            reservationId, Guid.NewGuid(), "Guest", "+5584999999999",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1), 2, "confirmed", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var handler = new GetReservationDetailQueryHandler(FakeReservationReader.WithDetail(detail));

        var result = await handler.Handle(new(reservationId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Id.Should().Be(reservationId);
    }

    [Fact]
    public async Task A_nonexistent_reservation_fails_with_ReservationNotFound()
    {
        var handler = new GetReservationDetailQueryHandler(FakeReservationReader.WithDetail(null));

        var result = await handler.Handle(new(Guid.NewGuid()), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(ReservationsErrorCodes.ReservationNotFound);
    }
}
