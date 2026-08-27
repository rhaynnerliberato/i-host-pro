using FluentAssertions;
using IHostPro.Contexts.Reservations.Domain;
using IHostPro.Contexts.Reservations.Domain.Enums;

namespace IHostPro.Contexts.Reservations.Tests.Unit.Domain;

public class ReservationTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset CheckIn = new(2026, 8, 10, 14, 0, 0, TimeSpan.FromHours(-3));
    private static readonly DateTimeOffset CheckOut = new(2026, 8, 13, 11, 0, 0, TimeSpan.FromHours(-3));

    private static Reservation CreateValid() =>
        Reservation.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Guest", "+5584999999999", CheckIn, CheckOut, 2, Now);

    [Fact]
    public void Create_with_valid_data_succeeds_and_starts_as_Confirmed()
    {
        var reservation = CreateValid();

        reservation.Status.Should().Be(ReservationStatus.Confirmed);
        reservation.GuestName.Should().Be("Guest");
        reservation.GuestPhone.Should().Be("+5584999999999");
    }

    [Fact]
    public void Create_normalizes_check_in_and_check_out_to_UTC()
    {
        var reservation = CreateValid();

        reservation.CheckInAt.Offset.Should().Be(TimeSpan.Zero);
        reservation.CheckOutAt.Offset.Should().Be(TimeSpan.Zero);
        reservation.CheckInAt.Should().Be(CheckIn.ToUniversalTime());
    }

    [Fact]
    public void Create_with_no_guest_phone_leaves_it_null()
    {
        var reservation = Reservation.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Guest", null, CheckIn, CheckOut, 2, Now);

        reservation.GuestPhone.Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_rejects_empty_guest_name(string guestName)
    {
        var act = () => Reservation.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), guestName, null, CheckIn, CheckOut, 2, Now);

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Create_rejects_non_positive_guest_count(int guestCount)
    {
        var act = () => Reservation.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Guest", null, CheckIn, CheckOut, guestCount, Now);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_rejects_check_out_not_after_check_in()
    {
        var act = () => Reservation.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Guest", null, CheckOut, CheckIn, 2, Now);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_rejects_check_out_exactly_equal_to_check_in()
    {
        var act = () => Reservation.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Guest", null, CheckIn, CheckIn, 2, Now);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ChangeProperty_reassigns_the_property_id()
    {
        var reservation = CreateValid();
        var newPropertyId = Guid.NewGuid();

        reservation.ChangeProperty(newPropertyId, Now.AddMinutes(1));

        reservation.PropertyId.Should().Be(newPropertyId);
    }

    [Fact]
    public void ChangeGuestPhone_with_null_removes_the_phone()
    {
        var reservation = CreateValid();

        reservation.ChangeGuestPhone(null, Now.AddMinutes(1));

        reservation.GuestPhone.Should().BeNull();
    }

    [Fact]
    public void ChangeGuestPhone_trims_whitespace()
    {
        var reservation = CreateValid();

        reservation.ChangeGuestPhone("  +5584988888888  ", Now.AddMinutes(1));

        reservation.GuestPhone.Should().Be("+5584988888888");
    }

    [Fact]
    public void Reschedule_rejects_check_out_not_after_check_in()
    {
        var reservation = CreateValid();

        var act = () => reservation.Reschedule(CheckOut, CheckIn, Now.AddMinutes(1));

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Reschedule_updates_both_dates_when_valid()
    {
        var reservation = CreateValid();
        var newCheckIn = CheckIn.AddDays(1);
        var newCheckOut = CheckOut.AddDays(1);

        reservation.Reschedule(newCheckIn, newCheckOut, Now.AddMinutes(1));

        reservation.CheckInAt.Should().Be(newCheckIn.ToUniversalTime());
        reservation.CheckOutAt.Should().Be(newCheckOut.ToUniversalTime());
    }

    [Fact]
    public void ChangeGuestCount_rejects_non_positive_value()
    {
        var reservation = CreateValid();

        var act = () => reservation.ChangeGuestCount(0, Now.AddMinutes(1));

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Cancel_from_Confirmed_transitions_to_Cancelled()
    {
        var reservation = CreateValid();

        reservation.Cancel(Now.AddMinutes(1));

        reservation.Status.Should().Be(ReservationStatus.Cancelled);
    }

    [Fact]
    public void Cancel_when_already_Cancelled_throws()
    {
        var reservation = CreateValid();
        reservation.Cancel(Now.AddMinutes(1));

        var act = () => reservation.Cancel(Now.AddMinutes(2));

        act.Should().Throw<InvalidOperationException>();
    }

    // ---- Fase 10, Checkpoint 1 (Guest Operations Foundation) ---------------

    [Fact]
    public void Close_from_Confirmed_transitions_to_Closed()
    {
        var reservation = CreateValid();

        reservation.Close(Now.AddMinutes(1));

        reservation.Status.Should().Be(ReservationStatus.Closed);
    }

    [Fact]
    public void Close_when_already_Closed_throws()
    {
        var reservation = CreateValid();
        reservation.Close(Now.AddMinutes(1));

        var act = () => reservation.Close(Now.AddMinutes(2));

        act.Should().Throw<InvalidOperationException>(
            "this guard is defense-in-depth — the handler is responsible for the real idempotent no-op BEFORE calling Close");
    }

    [Fact]
    public void Close_when_Cancelled_throws()
    {
        var reservation = CreateValid();
        reservation.Cancel(Now.AddMinutes(1));

        var act = () => reservation.Close(Now.AddMinutes(2));

        act.Should().Throw<InvalidOperationException>(
            "this guard is defense-in-depth — the handler is responsible for the specific invariant-violation exception BEFORE calling Close");
    }

    // ---- Fase 9, Checkpoint 3.2 ("Airbnb Deterministic Foundation") --------

    [Fact]
    public void Create_defaults_Source_to_Manual_with_no_ExternalReservationId()
    {
        var reservation = CreateValid();

        reservation.Source.Should().Be(ReservationSource.Manual);
        reservation.ExternalReservationId.Should().BeNull();
    }

    [Fact]
    public void CreateImported_sets_Source_to_Airbnb_and_persists_the_external_id()
    {
        var reservation = Reservation.CreateImported(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Guest", null, CheckIn, CheckOut, 2, "AIRBNB-EXT-1", Now);

        reservation.Source.Should().Be(ReservationSource.Airbnb);
        reservation.ExternalReservationId.Should().Be("AIRBNB-EXT-1");
        reservation.Status.Should().Be(ReservationStatus.Confirmed);
    }

    [Fact]
    public void CreateImported_trims_the_external_reservation_id()
    {
        var reservation = Reservation.CreateImported(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Guest", null, CheckIn, CheckOut, 2, "  AIRBNB-EXT-2  ", Now);

        reservation.ExternalReservationId.Should().Be("AIRBNB-EXT-2");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void CreateImported_rejects_empty_external_reservation_id(string externalReservationId)
    {
        var act = () => Reservation.CreateImported(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Guest", null, CheckIn, CheckOut, 2, externalReservationId, Now);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void CreateImported_rejects_empty_guest_name()
    {
        var act = () => Reservation.CreateImported(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "", null, CheckIn, CheckOut, 2, "AIRBNB-EXT-3", Now);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void CreateImported_rejects_check_out_not_after_check_in()
    {
        var act = () => Reservation.CreateImported(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Guest", null, CheckOut, CheckIn, 2, "AIRBNB-EXT-4", Now);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void CreateImported_rejects_non_positive_guest_count()
    {
        var act = () => Reservation.CreateImported(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Guest", null, CheckIn, CheckOut, 0, "AIRBNB-EXT-5", Now);

        act.Should().Throw<ArgumentException>();
    }
}
