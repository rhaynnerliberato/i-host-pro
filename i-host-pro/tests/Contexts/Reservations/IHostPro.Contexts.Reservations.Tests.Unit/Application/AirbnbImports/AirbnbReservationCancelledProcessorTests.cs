using FluentAssertions;
using IHostPro.Contexts.ExternalIntegrations.Contracts;
using IHostPro.Contexts.Reservations.Application.AirbnbImports;
using IHostPro.Contexts.Reservations.Contracts;
using IHostPro.Contexts.Reservations.Domain;
using IHostPro.Contexts.Reservations.Domain.Enums;
using IHostPro.Contexts.Reservations.Tests.Unit.Application.Reservations;
using IHostPro.Contexts.Reservations.Tests.Unit.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace IHostPro.Contexts.Reservations.Tests.Unit.Application.AirbnbImports;

/// <summary>
/// Fase 9, Checkpoint 3.2.1 — formalizes, with deterministic tests, the
/// PERMANENT NO-OP decision for an unknown <c>ExternalReservationId</c>: no
/// <see cref="Reservation"/> is ever created, no exception is thrown, and no
/// <see cref="ReservationCancelled"/> is published. Same pre-existing
/// behavior as Checkpoint 3.2 — these tests close the coverage gap the
/// corrective gate found.
/// </summary>
public class AirbnbReservationCancelledProcessorTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);

    private static AirbnbReservationCancelled BuildEvent(string externalReservationId = "AIRBNB-1") => new()
    {
        TenantId = TenantId,
        AggregateId = Guid.NewGuid(),
        AggregateType = "AirbnbReservation",
        CorrelationId = Guid.NewGuid(),
        ActorType = "Integration",
        ExternalReservationId = externalReservationId,
        OccurredAtUtc = Now,
    };

    private static Reservation CreateImportedReservation() =>
        Reservation.CreateImported(
            Guid.NewGuid(), TenantId, Guid.NewGuid(), "Guest", null, Now.AddDays(1), Now.AddDays(3), 2, "AIRBNB-1", Now);

    private static AirbnbReservationCancelledProcessor CreateProcessor(
        Guid? externalIdentityResult, RecordingReservationRepository repository, FakeIntegrationEventCollector collector)
    {
        var reader = FakeReservationReader.WithExternalIdentityResult(externalIdentityResult);
        return new AirbnbReservationCancelledProcessor(
            reader, repository, collector, new PassThroughReservationsTransactionExecutor(), new FixedTimeProvider(Now.AddMinutes(5)),
            NullLogger<AirbnbReservationCancelledProcessor>.Instance);
    }

    // ---- B. Unknown ExternalReservationId — permanent no-op ----------------

    [Fact]
    public async Task HandleAsync_unknown_external_id_creates_no_Reservation()
    {
        var repository = RecordingReservationRepository.WithReservation(null);
        var collector = new FakeIntegrationEventCollector();
        var processor = CreateProcessor(externalIdentityResult: null, repository, collector);

        await processor.HandleAsync(BuildEvent(), CancellationToken.None);

        repository.AddCallCount.Should().Be(0);
        repository.UpdateCallCount.Should().Be(0);
    }

    [Fact]
    public async Task HandleAsync_unknown_external_id_never_throws()
    {
        var repository = RecordingReservationRepository.WithReservation(null);
        var collector = new FakeIntegrationEventCollector();
        var processor = CreateProcessor(externalIdentityResult: null, repository, collector);

        var act = async () => await processor.HandleAsync(BuildEvent(), CancellationToken.None);

        await act.Should().NotThrowAsync("an unknown external id must never fail the message (no Wolverine retry/DLQ)");
    }

    [Fact]
    public async Task HandleAsync_unknown_external_id_publishes_no_ReservationCancelled()
    {
        var repository = RecordingReservationRepository.WithReservation(null);
        var collector = new FakeIntegrationEventCollector();
        var processor = CreateProcessor(externalIdentityResult: null, repository, collector);

        await processor.HandleAsync(BuildEvent(), CancellationToken.None);

        collector.EnqueuedEvents.Should().BeEmpty();
    }

    // ---- Happy path: found and Confirmed -----------------------------------

    [Fact]
    public async Task HandleAsync_cancels_a_Confirmed_reservation_and_publishes_ReservationCancelled()
    {
        var reservation = CreateImportedReservation();
        var repository = RecordingReservationRepository.WithReservation(reservation);
        var collector = new FakeIntegrationEventCollector();
        var processor = CreateProcessor(reservation.Id, repository, collector);

        await processor.HandleAsync(BuildEvent(), CancellationToken.None);

        reservation.Status.Should().Be(ReservationStatus.Cancelled);
        repository.UpdateCallCount.Should().Be(1);
        var published = collector.EnqueuedEvents.Should().ContainSingle().Which.Should().BeOfType<ReservationCancelled>().Which;
        published.ReservationId.Should().Be(reservation.Id);
    }

    // ---- D. Duplicate cancellation when already Cancelled — no-op ---------

    [Fact]
    public async Task HandleAsync_duplicate_cancellation_when_already_Cancelled_is_a_no_op()
    {
        var reservation = CreateImportedReservation();
        reservation.Cancel(Now.AddMinutes(1));
        var repository = RecordingReservationRepository.WithReservation(reservation);
        var collector = new FakeIntegrationEventCollector();
        var processor = CreateProcessor(reservation.Id, repository, collector);

        await processor.HandleAsync(BuildEvent(), CancellationToken.None);

        repository.UpdateCallCount.Should().Be(0, "an already-Cancelled reservation must never be updated again");
        collector.EnqueuedEvents.Should().BeEmpty("an already-Cancelled reservation must never publish a duplicate ReservationCancelled");
    }
}
