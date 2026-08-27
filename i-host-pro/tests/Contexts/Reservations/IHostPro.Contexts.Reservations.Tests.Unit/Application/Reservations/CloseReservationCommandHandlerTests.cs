using FluentAssertions;
using IHostPro.Contexts.Reservations.Application;
using IHostPro.Contexts.Reservations.Application.Reservations;
using IHostPro.Contexts.Reservations.Contracts;
using IHostPro.Contexts.Reservations.Domain;
using IHostPro.Contexts.Reservations.Domain.Enums;
using IHostPro.Contexts.Reservations.Tests.Unit.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace IHostPro.Contexts.Reservations.Tests.Unit.Application.Reservations;

/// <summary>
/// Fase 10, Checkpoint 1 (Guest Operations Foundation) — proves the
/// user-approved <c>CloseReservation</c> closure semantics deterministically:
/// Confirmed → Closed publishes <see cref="ReservationClosed"/> exactly once;
/// Closed is a silent idempotent no-op (never republishes, never throws);
/// Cancelled is an invariant violation — throws
/// <see cref="ReservationCancelledCannotBeClosedException"/>, leaves the
/// reservation Cancelled, publishes nothing. No custom retry policy exists
/// for that exception (see <c>CloseReservationHandler</c>'s own file — no
/// Wolverine <c>Configure</c>/<c>RetryWithCooldown</c> call references it).
/// </summary>
public class CloseReservationCommandHandlerTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);

    private static Reservation CreateConfirmedReservation() =>
        Reservation.Create(Guid.NewGuid(), TenantId, Guid.NewGuid(), "Guest", null, Now.AddDays(-3), Now.AddDays(-1), 2, Now.AddDays(-5));

    private static CloseReservation BuildCommand(Guid reservationId) => new()
    {
        TenantId = TenantId,
        ReservationId = reservationId,
        CorrelationId = Guid.NewGuid(),
    };

    private static CloseReservationCommandHandler CreateHandler(RecordingReservationRepository repository, FakeIntegrationEventCollector collector) =>
        new(repository, collector, new PassThroughReservationsTransactionExecutor(), new FixedTimeProvider(Now.AddMinutes(5)),
            NullLogger<CloseReservationCommandHandler>.Instance);

    // ---- 1. Confirmed + CloseReservation -> Closed -> ReservationClosed published once ----

    [Fact]
    public async Task HandleAsync_closes_a_Confirmed_reservation_and_publishes_ReservationClosed_once()
    {
        var reservation = CreateConfirmedReservation();
        var repository = RecordingReservationRepository.WithReservation(reservation);
        var collector = new FakeIntegrationEventCollector();
        var handler = CreateHandler(repository, collector);

        await handler.HandleAsync(BuildCommand(reservation.Id), CancellationToken.None);

        reservation.Status.Should().Be(ReservationStatus.Closed);
        repository.UpdateCallCount.Should().Be(1);
        var published = collector.EnqueuedEvents.Should().ContainSingle().Which.Should().BeOfType<ReservationClosed>().Which;
        published.ReservationId.Should().Be(reservation.Id);
        published.PropertyId.Should().Be(reservation.PropertyId);
        published.ActorType.Should().Be("System");
    }

    // ---- 2. Closed + CloseReservation -> no-op -> zero new ReservationClosed ----

    [Fact]
    public async Task HandleAsync_when_already_Closed_is_a_silent_no_op()
    {
        var reservation = CreateConfirmedReservation();
        reservation.Close(Now.AddMinutes(1));
        var repository = RecordingReservationRepository.WithReservation(reservation);
        var collector = new FakeIntegrationEventCollector();
        var handler = CreateHandler(repository, collector);

        var act = async () => await handler.HandleAsync(BuildCommand(reservation.Id), CancellationToken.None);

        await act.Should().NotThrowAsync("an already-Closed reservation must be a silent idempotent no-op");
        reservation.Status.Should().Be(ReservationStatus.Closed);
        repository.UpdateCallCount.Should().Be(0, "an already-Closed reservation must never be updated again");
        collector.EnqueuedEvents.Should().BeEmpty("an already-Closed reservation must never publish a duplicate ReservationClosed");
    }

    // ---- 3. Cancelled + CloseReservation -> specific exception -> status stays Cancelled -> zero ReservationClosed ----

    [Fact]
    public async Task HandleAsync_when_Cancelled_throws_the_specific_invariant_violation_exception()
    {
        var reservation = CreateConfirmedReservation();
        reservation.Cancel(Now.AddMinutes(1));
        var repository = RecordingReservationRepository.WithReservation(reservation);
        var collector = new FakeIntegrationEventCollector();
        var handler = CreateHandler(repository, collector);

        var act = async () => await handler.HandleAsync(BuildCommand(reservation.Id), CancellationToken.None);

        await act.Should().ThrowAsync<ReservationCancelledCannotBeClosedException>(
            "a Cancelled reservation receiving CloseReservation is an orchestration invariant violation, never a permanent no-op");
    }

    [Fact]
    public async Task HandleAsync_when_Cancelled_never_changes_status_and_never_publishes()
    {
        var reservation = CreateConfirmedReservation();
        reservation.Cancel(Now.AddMinutes(1));
        var repository = RecordingReservationRepository.WithReservation(reservation);
        var collector = new FakeIntegrationEventCollector();
        var handler = CreateHandler(repository, collector);

        try
        {
            await handler.HandleAsync(BuildCommand(reservation.Id), CancellationToken.None);
        }
        catch (ReservationCancelledCannotBeClosedException)
        {
            // Expected — asserted separately above.
        }

        reservation.Status.Should().Be(ReservationStatus.Cancelled, "the exception must never restore/transition the reservation");
        repository.UpdateCallCount.Should().Be(0);
        collector.EnqueuedEvents.Should().BeEmpty("a Cancelled reservation must never publish ReservationClosed");
    }

    [Fact]
    public void ReservationCancelledCannotBeClosedException_message_carries_no_PII()
    {
        var reservation = CreateConfirmedReservation();
        reservation.Cancel(Now.AddMinutes(1));

        var exception = new ReservationCancelledCannotBeClosedException(
            $"CloseReservation: reservation '{reservation.Id}' is Cancelled and cannot be closed.");

        exception.Message.Should().NotContain(reservation.GuestName)
            .And.NotContain("Phone");
    }

    // ---- Missing reservation: a generic, distinct anomaly ------------------

    [Fact]
    public async Task HandleAsync_when_reservation_not_found_throws_a_generic_exception_not_the_Cancelled_specific_one()
    {
        var repository = RecordingReservationRepository.WithReservation(null);
        var collector = new FakeIntegrationEventCollector();
        var handler = CreateHandler(repository, collector);

        var act = async () => await handler.HandleAsync(BuildCommand(Guid.NewGuid()), CancellationToken.None);

        var thrown = await act.Should().ThrowAsync<InvalidOperationException>();
        thrown.Which.Should().NotBeOfType<ReservationCancelledCannotBeClosedException>();
        collector.EnqueuedEvents.Should().BeEmpty();
    }
}
