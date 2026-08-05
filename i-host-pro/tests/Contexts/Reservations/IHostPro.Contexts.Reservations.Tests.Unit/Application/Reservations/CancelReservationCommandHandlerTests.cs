using FluentAssertions;
using IHostPro.Contexts.Reservations.Application.Errors;
using IHostPro.Contexts.Reservations.Application.Reservations;
using IHostPro.Contexts.Reservations.Contracts;
using IHostPro.Contexts.Reservations.Domain;
using IHostPro.Contexts.Reservations.Tests.Unit.Infrastructure;

namespace IHostPro.Contexts.Reservations.Tests.Unit.Application.Reservations;

public class CancelReservationCommandHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset CheckIn = new(2026, 8, 10, 14, 0, 0, TimeSpan.FromHours(-3));
    private static readonly DateTimeOffset CheckOut = new(2026, 8, 13, 11, 0, 0, TimeSpan.FromHours(-3));
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid ActorId = Guid.NewGuid();
    private static readonly Guid PropertyId = Guid.NewGuid();
    private static readonly Guid ReservationId = Guid.NewGuid();

    private sealed record Fixture(
        FakeReservationAuditWriter AuditWriter, FakeIntegrationEventCollector EventCollector, CancelReservationCommandHandler Handler);

    private static Reservation ExistingReservation(bool cancelled = false)
    {
        var reservation = Reservation.Create(ReservationId, TenantId, PropertyId, "Guest", null, CheckIn, CheckOut, 2, Now);
        if (cancelled)
            reservation.Cancel(Now);
        return reservation;
    }

    private static Fixture CreateFixture(Reservation? existing)
    {
        var auditWriter = new FakeReservationAuditWriter();
        var eventCollector = new FakeIntegrationEventCollector();
        var handler = new CancelReservationCommandHandler(
            new PassThroughCancelReservationExecutor(),
            FakeReservationRepository.WithReservation(existing),
            auditWriter,
            eventCollector,
            new FixedTimeProvider(Now.AddMinutes(1)));

        return new Fixture(auditWriter, eventCollector, handler);
    }

    [Fact]
    public async Task A_confirmed_reservation_transitions_to_cancelled()
    {
        var fixture = CreateFixture(ExistingReservation());

        var result = await fixture.Handler.Handle(new(TenantId, ActorId, ReservationId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be("cancelled");
    }

    [Fact]
    public async Task A_nonexistent_reservation_fails_with_ReservationNotFound()
    {
        var fixture = CreateFixture(existing: null);

        var result = await fixture.Handler.Handle(new(TenantId, ActorId, ReservationId), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(ReservationsErrorCodes.ReservationNotFound);
    }

    [Fact]
    public async Task An_already_cancelled_reservation_fails_with_ReservationAlreadyCancelled()
    {
        var fixture = CreateFixture(ExistingReservation(cancelled: true));

        var result = await fixture.Handler.Handle(new(TenantId, ActorId, ReservationId), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(ReservationsErrorCodes.ReservationAlreadyCancelled);
    }

    [Fact]
    public async Task Cancellation_writes_exactly_one_audit_entry_and_enqueues_exactly_one_ReservationCancelled_event()
    {
        var fixture = CreateFixture(ExistingReservation());

        var result = await fixture.Handler.Handle(new(TenantId, ActorId, ReservationId), CancellationToken.None);

        fixture.AuditWriter.RecordedEntries.Should().ContainSingle();
        fixture.AuditWriter.RecordedEntries[0].ActionCode.Should().Be("reservation_cancelled");

        var events = fixture.EventCollector.EnqueuedEvents.OfType<ReservationCancelled>().ToArray();
        events.Should().ContainSingle();
        events[0].ReservationId.Should().Be(result.Value.Id);
        events[0].PropertyId.Should().Be(PropertyId);
    }

    [Fact]
    public void No_guest_name_phone_or_schedule_content_ever_reaches_the_ReservationCancelled_event()
    {
        typeof(ReservationCancelled).GetProperties().Select(p => p.Name)
            .Should().NotContain(new[] { "GuestName", "GuestPhone", "CheckInAt", "CheckOutAt", "GuestCount" });
    }

    [Fact]
    public async Task The_ReservationCancelled_events_real_serialized_payload_carries_no_guest_name_phone_or_schedule_content()
    {
        var fixture = CreateFixture(ExistingReservation());

        await fixture.Handler.Handle(new(TenantId, ActorId, ReservationId), CancellationToken.None);

        var evt = fixture.EventCollector.EnqueuedEvents.OfType<ReservationCancelled>().Single();
        var json = System.Text.Json.JsonSerializer.Serialize(evt, evt.GetType());

        json.Should().NotContain("Guest");
        json.Should().NotContain(CheckIn.ToString("O"));
        json.Should().NotContain(CheckOut.ToString("O"));
    }
}
