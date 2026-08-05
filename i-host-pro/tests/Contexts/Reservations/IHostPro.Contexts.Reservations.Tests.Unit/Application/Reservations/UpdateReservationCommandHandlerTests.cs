using FluentAssertions;
using IHostPro.Contexts.PropertyManagement.Contracts;
using IHostPro.Contexts.Reservations.Application;
using IHostPro.Contexts.Reservations.Application.Errors;
using IHostPro.Contexts.Reservations.Application.Reservations;
using IHostPro.Contexts.Reservations.Contracts;
using IHostPro.Contexts.Reservations.Domain;
using IHostPro.Contexts.Reservations.Tests.Unit.Infrastructure;

namespace IHostPro.Contexts.Reservations.Tests.Unit.Application.Reservations;

public class UpdateReservationCommandHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset CheckIn = new(2026, 8, 10, 14, 0, 0, TimeSpan.FromHours(-3));
    private static readonly DateTimeOffset CheckOut = new(2026, 8, 13, 11, 0, 0, TimeSpan.FromHours(-3));
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid ActorId = Guid.NewGuid();
    private static readonly Guid PropertyId = Guid.NewGuid();
    private static readonly Guid ReservationId = Guid.NewGuid();

    private sealed record Fixture(
        FakeReservationRepository Repository,
        FakeReservationAuditWriter AuditWriter,
        FakeIntegrationEventCollector EventCollector,
        UpdateReservationCommandHandler Handler);

    private static Reservation ExistingReservation(bool cancelled = false)
    {
        var reservation = Reservation.Create(ReservationId, TenantId, PropertyId, "Guest", "+5584999999999", CheckIn, CheckOut, 2, Now);
        if (cancelled)
            reservation.Cancel(Now);
        return reservation;
    }

    private static Fixture CreateFixture(
        Reservation? existing, PropertyReservationEligibility? eligibility = null, bool hasConflict = false,
        uint snapshotXmin = 1, uint? currentXmin = null)
    {
        eligibility ??= new PropertyReservationEligibility(PropertyId, IsActive: true, Capacity: 4);

        var repository = FakeReservationRepository.WithReservation(existing);
        var auditWriter = new FakeReservationAuditWriter();
        var eventCollector = new FakeIntegrationEventCollector();
        var handler = new UpdateReservationCommandHandler(
            FakeReservationReader.WithUpdateSnapshot(existing, snapshotXmin, currentXmin),
            FakePropertyReservationEligibilityReader.With(eligibility),
            new PassThroughUpdateReservationExecutor(),
            FakeReservationConflictGuard.WithConflict(hasConflict),
            repository,
            auditWriter,
            eventCollector,
            new FixedTimeProvider(Now.AddMinutes(1)));

        return new Fixture(repository, auditWriter, eventCollector, handler);
    }

    private static UpdateReservationCommand Command(
        Optional<Guid>? propertyId = null, Optional<string>? guestName = null, Optional<string?>? guestPhone = null,
        Optional<DateTimeOffset>? checkInAt = null, Optional<DateTimeOffset>? checkOutAt = null, Optional<int>? guestCount = null) =>
        new(TenantId, ActorId, ReservationId,
            propertyId ?? Optional<Guid>.Unset,
            guestName ?? Optional<string>.Unset,
            guestPhone ?? Optional<string?>.Unset,
            checkInAt ?? Optional<DateTimeOffset>.Unset,
            checkOutAt ?? Optional<DateTimeOffset>.Unset,
            guestCount ?? Optional<int>.Unset);

    [Fact]
    public async Task Changing_guest_name_updates_it_and_records_ChangedFields()
    {
        var fixture = CreateFixture(ExistingReservation());

        var result = await fixture.Handler.Handle(Command(guestName: Optional<string>.Of("New Name")), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.GuestName.Should().Be("New Name");
        var evt = fixture.EventCollector.EnqueuedEvents.OfType<ReservationUpdated>().Single();
        evt.ChangedFields.Should().BeEquivalentTo(["guest_name"]);
    }

    [Fact]
    public async Task A_no_op_request_with_a_value_equal_to_current_does_not_emit_audit_or_event()
    {
        var fixture = CreateFixture(ExistingReservation());

        var result = await fixture.Handler.Handle(Command(guestName: Optional<string>.Of("Guest")), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        fixture.AuditWriter.RecordedEntries.Should().BeEmpty();
        fixture.EventCollector.EnqueuedEvents.Should().BeEmpty();
    }

    [Fact]
    public async Task Nothing_set_fails_with_NoChangesProvided()
    {
        var fixture = CreateFixture(ExistingReservation());

        var result = await fixture.Handler.Handle(Command(), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(ReservationsErrorCodes.NoChangesProvided);
        fixture.Repository.GetByIdCallCount.Should().Be(0);
    }

    [Fact]
    public async Task A_nonexistent_reservation_fails_with_ReservationNotFound()
    {
        var fixture = CreateFixture(existing: null);

        var result = await fixture.Handler.Handle(Command(guestName: Optional<string>.Of("New Name")), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(ReservationsErrorCodes.ReservationNotFound);
    }

    [Fact]
    public async Task A_cancelled_reservation_rejects_every_patch_including_a_value_matching_no_op()
    {
        var fixture = CreateFixture(ExistingReservation(cancelled: true));

        var result = await fixture.Handler.Handle(Command(guestName: Optional<string>.Of("Guest")), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(ReservationsErrorCodes.CancelledReservationCannotBeModified);
    }

    [Fact]
    public async Task Guest_count_exceeding_the_final_propertys_capacity_fails_with_PropertyCapacityExceeded()
    {
        var fixture = CreateFixture(ExistingReservation(), new PropertyReservationEligibility(PropertyId, IsActive: true, Capacity: 1));

        var result = await fixture.Handler.Handle(Command(guestCount: Optional<int>.Of(2)), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(ReservationsErrorCodes.PropertyCapacityExceeded);
    }

    [Fact]
    public async Task Reassigning_to_an_inactive_property_fails_with_PropertyNotActive()
    {
        var newPropertyId = Guid.NewGuid();
        var fixture = CreateFixture(ExistingReservation(), new PropertyReservationEligibility(newPropertyId, IsActive: false, Capacity: 4));

        var result = await fixture.Handler.Handle(Command(propertyId: Optional<Guid>.Of(newPropertyId)), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(ReservationsErrorCodes.PropertyNotActive);
    }

    [Fact]
    public async Task An_overlapping_reservation_fails_with_ReservationDateConflict()
    {
        var fixture = CreateFixture(ExistingReservation(), hasConflict: true);

        var result = await fixture.Handler.Handle(Command(guestName: Optional<string>.Of("New Name")), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(ReservationsErrorCodes.ReservationDateConflict);
    }

    [Fact]
    public async Task Removing_guest_phone_with_explicit_null_updates_it_and_records_ChangedFields()
    {
        var fixture = CreateFixture(ExistingReservation());

        var result = await fixture.Handler.Handle(Command(guestPhone: Optional<string?>.Of(null)), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.GuestPhone.Should().BeNull();
        var evt = fixture.EventCollector.EnqueuedEvents.OfType<ReservationUpdated>().Single();
        evt.ChangedFields.Should().BeEquivalentTo(["guest_phone"]);
    }

    [Fact]
    public async Task Changing_only_check_in_records_only_check_in_at_in_ChangedFields()
    {
        var fixture = CreateFixture(ExistingReservation());
        var newCheckIn = CheckIn.AddHours(-1);

        var result = await fixture.Handler.Handle(Command(checkInAt: Optional<DateTimeOffset>.Of(newCheckIn)), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var evt = fixture.EventCollector.EnqueuedEvents.OfType<ReservationUpdated>().Single();
        evt.ChangedFields.Should().BeEquivalentTo(["check_in_at"]);
    }

    [Fact]
    public async Task A_row_changed_between_the_snapshot_and_the_write_transaction_fails_with_ReservationConcurrencyConflict_without_auditing_or_publishing()
    {
        var fixture = CreateFixture(ExistingReservation(), snapshotXmin: 1, currentXmin: 2);

        var result = await fixture.Handler.Handle(Command(guestName: Optional<string>.Of("New Name")), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(ReservationsErrorCodes.ReservationConcurrencyConflict);
        fixture.AuditWriter.RecordedEntries.Should().BeEmpty();
        fixture.EventCollector.EnqueuedEvents.Should().BeEmpty();
    }

    [Fact]
    public void No_guest_name_phone_or_schedule_content_ever_reaches_the_ReservationUpdated_event()
    {
        typeof(ReservationUpdated).GetProperties().Select(p => p.Name)
            .Should().NotContain(new[] { "GuestName", "GuestPhone", "CheckInAt", "CheckOutAt", "GuestCount" });
    }

    [Fact]
    public async Task The_ReservationUpdated_events_real_serialized_payload_carries_no_guest_name_phone_or_schedule_content()
    {
        const string newGuestName = "Sensitive New Name";
        var fixture = CreateFixture(ExistingReservation());

        var result = await fixture.Handler.Handle(Command(guestName: Optional<string>.Of(newGuestName)), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var evt = fixture.EventCollector.EnqueuedEvents.OfType<ReservationUpdated>().Single();
        var json = System.Text.Json.JsonSerializer.Serialize(evt, evt.GetType());

        json.Should().NotContain(newGuestName);
        json.Should().NotContain("Guest"); // the reservation's original guest name (ExistingReservation())
        json.Should().NotContain("+5584999999999"); // the reservation's original guest phone
        json.Should().NotContain(CheckIn.ToString("O"));
        json.Should().NotContain(CheckOut.ToString("O"));
    }
}
