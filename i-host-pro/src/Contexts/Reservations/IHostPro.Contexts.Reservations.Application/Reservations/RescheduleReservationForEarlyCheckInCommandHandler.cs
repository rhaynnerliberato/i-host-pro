using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.Reservations.Contracts;
using IHostPro.Contexts.Reservations.Domain;
using Microsoft.Extensions.Logging;

namespace IHostPro.Contexts.Reservations.Application.Reservations;

/// <summary>
/// Reacts to Workflow Orchestration's <see cref="RescheduleReservationForEarlyCheckIn"/>
/// command (Fase 10, Checkpoint 3 — Early Check-in/Late Checkout), sent only
/// after Guest Operations' own synchronous evaluation already approved the
/// request. Mirrors <c>CloseReservationCommandHandler</c>'s own shape: the
/// reservation's own conflict guard is re-run here (Guest Operations' own
/// <c>IReservationScheduleReader</c> read is an eligibility check, never a
/// substitute for Reservations' own transactional invariants — a real
/// conflict re-detected at this point is an internal-chain anomaly, not a
/// normal validation failure, and relies exclusively on Wolverine's default
/// single-attempt/dead-letter handling, same as <c>CloseReservationCommandHandler</c>'s
/// own "missing reservation"/"Cancelled" anomalies). Publishes
/// <see cref="ReservationUpdated"/> (never a new event type) so Dashboard's
/// own existing projection stays in sync automatically — Reservation remains
/// the single source of truth for the calendar.
/// </summary>
public sealed class RescheduleReservationForEarlyCheckInCommandHandler : IRescheduleReservationForEarlyCheckInHandler
{
    private readonly IRepository<Reservation, Guid> _repository;
    private readonly IReservationConflictGuard _conflictGuard;
    private readonly IIntegrationEventCollector _eventCollector;
    private readonly IReservationsTransactionExecutor _transactionExecutor;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<RescheduleReservationForEarlyCheckInCommandHandler> _logger;

    public RescheduleReservationForEarlyCheckInCommandHandler(
        IRepository<Reservation, Guid> repository,
        IReservationConflictGuard conflictGuard,
        IIntegrationEventCollector eventCollector,
        IReservationsTransactionExecutor transactionExecutor,
        TimeProvider timeProvider,
        ILogger<RescheduleReservationForEarlyCheckInCommandHandler> logger)
    {
        _repository = repository;
        _conflictGuard = conflictGuard;
        _eventCollector = eventCollector;
        _transactionExecutor = transactionExecutor;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public Task HandleAsync(RescheduleReservationForEarlyCheckIn command, CancellationToken cancellationToken) =>
        _transactionExecutor.ExecuteAsync(async () =>
        {
            var reservation = await _repository.GetByIdAsync(command.ReservationId, cancellationToken);

            if (reservation is null)
            {
                throw new InvalidOperationException(
                    $"RescheduleReservationForEarlyCheckIn: reservation '{command.ReservationId}' not found for " +
                    $"tenant '{command.TenantId}' — relies on Wolverine's own default redelivery behavior; " +
                    "no custom retry policy introduced.");
            }

            await _conflictGuard.AcquirePropertyLockAsync(command.TenantId, reservation.PropertyId, cancellationToken);

            var hasConflict = await _conflictGuard.HasConflictingReservationAsync(
                command.TenantId, reservation.PropertyId, command.NewCheckInAt, reservation.CheckOutAt,
                excludeReservationId: reservation.Id, cancellationToken);

            if (hasConflict)
            {
                throw new InvalidOperationException(
                    $"RescheduleReservationForEarlyCheckIn: a real schedule conflict was re-detected for reservation " +
                    $"'{command.ReservationId}' at mutation time — Guest Operations' own eligibility read is TOCTOU-accepted, " +
                    "never a substitute for this transactional re-check; relies on Wolverine's own default error handling.");
            }

            var now = _timeProvider.GetUtcNow();
            reservation.Reschedule(command.NewCheckInAt, reservation.CheckOutAt, now);
            _repository.Update(reservation);

            _eventCollector.Enqueue(new ReservationUpdated
            {
                TenantId = command.TenantId,
                AggregateId = reservation.Id,
                AggregateType = "Reservation",
                CorrelationId = command.CorrelationId,
                CausationId = command.CausationId,
                ActorType = "System",
                ReservationId = reservation.Id,
                ChangedFields = ["check_in_at"],
                CheckInAt = reservation.CheckInAt,
                CheckOutAt = reservation.CheckOutAt,
            });

            _logger.LogInformation(
                "Reservation rescheduled for early check-in, tenant {TenantId} reservationId {ReservationId}",
                command.TenantId, reservation.Id);

            return true;
        }, cancellationToken);
}
