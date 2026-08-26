using FluentAssertions;
using IHostPro.Contexts.ExternalIntegrations.Contracts;
using IHostPro.Contexts.Reservations.Application.AirbnbImports;
using IHostPro.Contexts.Reservations.Contracts;
using IHostPro.Contexts.Reservations.Domain;
using IHostPro.Contexts.Reservations.Tests.Unit.Application.Reservations;
using IHostPro.Contexts.Reservations.Tests.Unit.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace IHostPro.Contexts.Reservations.Tests.Unit.Application.AirbnbImports;

/// <summary>
/// Fase 9, Checkpoint 3.2.1 — formalizes, with deterministic tests, the
/// PERMANENT NO-OP decision for an unknown <c>ExternalReservationId</c>: no
/// <see cref="Reservation"/> is ever created, no exception is thrown (so
/// Wolverine never retries/dead-letters it), and no
/// <see cref="ReservationUpdated"/> is published. This is the exact behavior
/// the processor already had since Checkpoint 3.2 — these tests close the
/// coverage gap the corrective gate found, not a behavior change.
/// </summary>
public class AirbnbReservationUpdatedProcessorTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset CheckIn = Now.AddDays(1);
    private static readonly DateTimeOffset CheckOut = Now.AddDays(3);

    private static AirbnbReservationUpdated BuildEvent(
        Guid propertyId, string guestName, DateTimeOffset checkInAt, DateTimeOffset checkOutAt, int guestCount,
        string externalReservationId = "AIRBNB-1") => new()
    {
        TenantId = TenantId,
        AggregateId = Guid.NewGuid(),
        AggregateType = "AirbnbReservation",
        CorrelationId = Guid.NewGuid(),
        ActorType = "Integration",
        ExternalReservationId = externalReservationId,
        PropertyId = propertyId,
        GuestName = guestName,
        CheckInAt = checkInAt,
        CheckOutAt = checkOutAt,
        GuestCount = guestCount,
        OccurredAtUtc = Now,
    };

    private static Reservation CreateImportedReservation(Guid propertyId, string guestName, int guestCount) =>
        Reservation.CreateImported(
            Guid.NewGuid(), TenantId, propertyId, guestName, null, CheckIn, CheckOut, guestCount, "AIRBNB-1", Now);

    private static AirbnbReservationUpdatedProcessor CreateProcessor(
        Reservation? existingResult, Guid? externalIdentityResult, RecordingReservationRepository repository,
        FakeIntegrationEventCollector collector)
    {
        var reader = FakeReservationReader.WithExternalIdentityResult(externalIdentityResult);
        return new AirbnbReservationUpdatedProcessor(
            reader, repository, collector, new PassThroughReservationsTransactionExecutor(), new FixedTimeProvider(Now.AddMinutes(5)),
            NullLogger<AirbnbReservationUpdatedProcessor>.Instance);
    }

    // ---- A. Unknown ExternalReservationId — permanent no-op ----------------

    [Fact]
    public async Task HandleAsync_unknown_external_id_creates_no_Reservation()
    {
        var repository = RecordingReservationRepository.WithReservation(null);
        var collector = new FakeIntegrationEventCollector();
        var processor = CreateProcessor(null, externalIdentityResult: null, repository, collector);

        await processor.HandleAsync(BuildEvent(Guid.NewGuid(), "Guest", CheckIn, CheckOut, 2), CancellationToken.None);

        repository.AddCallCount.Should().Be(0);
        repository.UpdateCallCount.Should().Be(0);
    }

    [Fact]
    public async Task HandleAsync_unknown_external_id_never_throws()
    {
        var repository = RecordingReservationRepository.WithReservation(null);
        var collector = new FakeIntegrationEventCollector();
        var processor = CreateProcessor(null, externalIdentityResult: null, repository, collector);

        var act = async () => await processor.HandleAsync(BuildEvent(Guid.NewGuid(), "Guest", CheckIn, CheckOut, 2), CancellationToken.None);

        await act.Should().NotThrowAsync("an unknown external id must never fail the message (no Wolverine retry/DLQ)");
    }

    [Fact]
    public async Task HandleAsync_unknown_external_id_publishes_no_ReservationUpdated()
    {
        var repository = RecordingReservationRepository.WithReservation(null);
        var collector = new FakeIntegrationEventCollector();
        var processor = CreateProcessor(null, externalIdentityResult: null, repository, collector);

        await processor.HandleAsync(BuildEvent(Guid.NewGuid(), "Guest", CheckIn, CheckOut, 2), CancellationToken.None);

        collector.EnqueuedEvents.Should().BeEmpty();
    }

    // ---- Happy path: found, something changed -----------------------------

    [Fact]
    public async Task HandleAsync_applies_changed_fields_and_publishes_ReservationUpdated()
    {
        var reservation = CreateImportedReservation(Guid.NewGuid(), "Old Name", 2);
        var repository = RecordingReservationRepository.WithReservation(reservation);
        var collector = new FakeIntegrationEventCollector();
        var processor = CreateProcessor(reservation, externalIdentityResult: reservation.Id, repository, collector);

        await processor.HandleAsync(BuildEvent(reservation.PropertyId, "New Name", CheckIn, CheckOut, 2), CancellationToken.None);

        reservation.GuestName.Should().Be("New Name");
        repository.UpdateCallCount.Should().Be(1);
        var published = collector.EnqueuedEvents.Should().ContainSingle().Which.Should().BeOfType<ReservationUpdated>().Which;
        published.ChangedFields.Should().Contain("guest_name");
    }

    // ---- C. Duplicate update with the same values — no-op -----------------

    [Fact]
    public async Task HandleAsync_duplicate_update_with_identical_values_is_a_no_op()
    {
        var reservation = CreateImportedReservation(Guid.NewGuid(), "Guest", 2);
        var repository = RecordingReservationRepository.WithReservation(reservation);
        var collector = new FakeIntegrationEventCollector();
        var processor = CreateProcessor(reservation, externalIdentityResult: reservation.Id, repository, collector);

        await processor.HandleAsync(
            BuildEvent(reservation.PropertyId, reservation.GuestName, reservation.CheckInAt, reservation.CheckOutAt, reservation.GuestCount),
            CancellationToken.None);

        repository.UpdateCallCount.Should().Be(0, "a no-op update must never mark the aggregate as updated");
        collector.EnqueuedEvents.Should().BeEmpty("an update that changes nothing must never publish a duplicate ReservationUpdated");
    }
}
