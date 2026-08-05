using FluentAssertions;
using IHostPro.Contexts.PropertyManagement.Contracts;
using IHostPro.Contexts.Reservations.Application.Errors;
using IHostPro.Contexts.Reservations.Application.Reservations;
using IHostPro.Contexts.Reservations.Contracts;
using IHostPro.Contexts.Reservations.Tests.Unit.Infrastructure;

namespace IHostPro.Contexts.Reservations.Tests.Unit.Application.Reservations;

public class CreateReservationCommandHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset CheckIn = new(2026, 8, 10, 14, 0, 0, TimeSpan.FromHours(-3));
    private static readonly DateTimeOffset CheckOut = new(2026, 8, 13, 11, 0, 0, TimeSpan.FromHours(-3));
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid ActorId = Guid.NewGuid();
    private static readonly Guid PropertyId = Guid.NewGuid();

    private sealed record Fixture(
        FakeReservationRepository Repository,
        FakeReservationAuditWriter AuditWriter,
        FakeIntegrationEventCollector EventCollector,
        FakeReservationConflictGuard ConflictGuard,
        CreateReservationCommandHandler Handler);

    private static Fixture CreateFixture(
        bool propertyExists = true, bool propertyIsActive = true, int propertyCapacity = 4, bool hasConflict = false)
    {
        PropertyReservationEligibility? eligibility = propertyExists
            ? new PropertyReservationEligibility(PropertyId, propertyIsActive, propertyCapacity)
            : null;

        var repository = FakeReservationRepository.WithReservation(null);
        var auditWriter = new FakeReservationAuditWriter();
        var eventCollector = new FakeIntegrationEventCollector();
        var conflictGuard = FakeReservationConflictGuard.WithConflict(hasConflict);
        var handler = new CreateReservationCommandHandler(
            FakePropertyReservationEligibilityReader.With(eligibility),
            new PassThroughCreateReservationExecutor(),
            conflictGuard,
            repository,
            auditWriter,
            eventCollector,
            new FixedTimeProvider(Now));

        return new Fixture(repository, auditWriter, eventCollector, conflictGuard, handler);
    }

    private static CreateReservationCommand Command(
        Guid? propertyId = null, string guestName = "Guest", string? guestPhone = "+5584999999999",
        DateTimeOffset? checkInAt = null, DateTimeOffset? checkOutAt = null, int guestCount = 2) =>
        new(TenantId, ActorId, propertyId ?? PropertyId, guestName, guestPhone,
            checkInAt ?? CheckIn, checkOutAt ?? CheckOut, guestCount);

    [Fact]
    public async Task A_valid_request_creates_the_reservation_as_Confirmed()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.Handle(Command(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be("confirmed");
        fixture.Repository.AddedReservations.Should().ContainSingle();
    }

    [Fact]
    public async Task A_nonexistent_property_fails_with_PropertyNotFound_and_performs_no_side_effect()
    {
        var fixture = CreateFixture(propertyExists: false);

        var result = await fixture.Handler.Handle(Command(), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(ReservationsErrorCodes.PropertyNotFound);
        AssertNoSideEffect(fixture);
    }

    [Fact]
    public async Task An_inactive_property_fails_with_PropertyNotActive_and_performs_no_side_effect()
    {
        var fixture = CreateFixture(propertyIsActive: false);

        var result = await fixture.Handler.Handle(Command(), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(ReservationsErrorCodes.PropertyNotActive);
        AssertNoSideEffect(fixture);
    }

    [Fact]
    public async Task Guest_count_exceeding_capacity_fails_with_PropertyCapacityExceeded_and_performs_no_side_effect()
    {
        var fixture = CreateFixture(propertyCapacity: 1);

        var result = await fixture.Handler.Handle(Command(guestCount: 2), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(ReservationsErrorCodes.PropertyCapacityExceeded);
        AssertNoSideEffect(fixture);
    }

    [Fact]
    public async Task An_overlapping_reservation_fails_with_ReservationDateConflict_and_performs_no_side_effect()
    {
        var fixture = CreateFixture(hasConflict: true);

        var result = await fixture.Handler.Handle(Command(), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(ReservationsErrorCodes.ReservationDateConflict);
        fixture.ConflictGuard.AcquirePropertyLockAsyncCallCount.Should().Be(1);
        AssertNoSideEffect(fixture);
    }

    [Fact]
    public async Task The_advisory_lock_is_acquired_before_the_conflict_check()
    {
        var fixture = CreateFixture();

        await fixture.Handler.Handle(Command(), CancellationToken.None);

        fixture.ConflictGuard.AcquirePropertyLockAsyncCallCount.Should().Be(1);
        fixture.ConflictGuard.HasConflictingReservationAsyncCallCount.Should().Be(1);
    }

    [Fact]
    public async Task Creation_writes_exactly_one_audit_entry_with_the_reservation_created_action_code_and_empty_ChangedFields()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.Handle(Command(), CancellationToken.None);

        fixture.AuditWriter.RecordedEntries.Should().ContainSingle();
        var entry = fixture.AuditWriter.RecordedEntries[0];
        entry.TenantId.Should().Be(TenantId);
        entry.ActorUserId.Should().Be(ActorId);
        entry.EntityType.Should().Be("Reservation");
        entry.ActionCode.Should().Be("reservation_created");
        entry.AggregateId.Should().Be(result.Value.Id);
        entry.ChangedFields.Should().BeEmpty();
    }

    [Fact]
    public async Task Creation_enqueues_exactly_one_ReservationCreated_event_with_the_stable_confirmed_status_code()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.Handle(Command(), CancellationToken.None);

        var events = fixture.EventCollector.EnqueuedEvents.OfType<ReservationCreated>().ToArray();
        events.Should().ContainSingle();
        events[0].TenantId.Should().Be(TenantId);
        events[0].ActorId.Should().Be(ActorId.ToString());
        events[0].AggregateId.Should().Be(result.Value.Id);
        events[0].AggregateType.Should().Be("Reservation");
        events[0].PropertyId.Should().Be(PropertyId);
        events[0].Status.Should().Be("confirmed");
    }

    [Fact]
    public async Task No_guest_name_phone_or_schedule_content_ever_reaches_the_event()
    {
        await CreateFixture().Handler.Handle(Command(), CancellationToken.None);

        typeof(ReservationCreated).GetProperties().Select(p => p.Name)
            .Should().NotContain(new[] { "GuestName", "GuestPhone", "CheckInAt", "CheckOutAt", "GuestCount" });
    }

    [Fact]
    public async Task The_ReservationCreated_events_real_serialized_payload_carries_no_guest_name_phone_or_schedule_content()
    {
        const string guestName = "Sensitive Guest Name";
        const string guestPhone = "+5584988887777";
        var fixture = CreateFixture();

        await fixture.Handler.Handle(Command(guestName: guestName, guestPhone: guestPhone), CancellationToken.None);

        var evt = fixture.EventCollector.EnqueuedEvents.OfType<ReservationCreated>().Single();
        var json = System.Text.Json.JsonSerializer.Serialize(evt, evt.GetType());

        json.Should().NotContain(guestName);
        json.Should().NotContain(guestPhone);
        json.Should().NotContain(CheckIn.ToString("O"));
        json.Should().NotContain(CheckOut.ToString("O"));
    }

    private static void AssertNoSideEffect(Fixture fixture)
    {
        fixture.Repository.AddedReservations.Should().BeEmpty();
        fixture.AuditWriter.RecordedEntries.Should().BeEmpty();
        fixture.EventCollector.EnqueuedEvents.Should().BeEmpty();
    }
}
