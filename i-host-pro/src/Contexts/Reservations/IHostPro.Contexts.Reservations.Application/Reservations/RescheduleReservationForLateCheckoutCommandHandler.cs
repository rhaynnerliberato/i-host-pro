using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.Reservations.Contracts;
using IHostPro.Contexts.Reservations.Domain;
using Microsoft.Extensions.Logging;

namespace IHostPro.Contexts.Reservations.Application.Reservations;

/// <summary>
/// Reacts to Workflow Orchestration's <see cref="RescheduleReservationForLateCheckout"/>
/// command (Fase 10, Checkpoint 3 — Early Check-in/Late Checkout) — mirrors
/// <see cref="RescheduleReservationForEarlyCheckInCommandHandler"/> exactly,
/// for the checkout side. Never sent for a <c>PendingPayment</c> outcome
/// (Guest Operations only publishes <c>LateCheckoutApproved</c>, which
/// triggers this command, once the request is fully and finally approved —
/// see ADR-024 amendment).
/// </summary>
public sealed class RescheduleReservationForLateCheckoutCommandHandler : IRescheduleReservationForLateCheckoutHandler
{
    private readonly IRepository<Reservation, Guid> _repository;
    private readonly IReservationConflictGuard _conflictGuard;
    private readonly IIntegrationEventCollector _eventCollector;
    private readonly IReservationsTransactionExecutor _transactionExecutor;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<RescheduleReservationForLateCheckoutCommandHandler> _logger;

    public RescheduleReservationForLateCheckoutCommandHandler(
        IRepository<Reservation, Guid> repository,
        IReservationConflictGuard conflictGuard,
        IIntegrationEventCollector eventCollector,
        IReservationsTransactionExecutor transactionExecutor,
        TimeProvider timeProvider,
        ILogger<RescheduleReservationForLateCheckoutCommandHandler> logger)
    {
        _repository = repository;
        _conflictGuard = conflictGuard;
        _eventCollector = eventCollector;
        _transactionExecutor = transactionExecutor;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public Task HandleAsync(RescheduleReservationForLateCheckout command, CancellationToken cancellationToken) =>
        _transactionExecutor.ExecuteAsync(async () =>
        {
            var reservation = await _repository.GetByIdAsync(command.ReservationId, cancellationToken);

            if (reservation is null)
            {
                throw new InvalidOperationException(
                    $"RescheduleReservationForLateCheckout: reservation '{command.ReservationId}' not found for " +
                    $"tenant '{command.TenantId}' — relies on Wolverine's own default redelivery behavior; " +
                    "no custom retry policy introduced.");
            }

            await _conflictGuard.AcquirePropertyLockAsync(command.TenantId, reservation.PropertyId, cancellationToken);

            var hasConflict = await _conflictGuard.HasConflictingReservationAsync(
                command.TenantId, reservation.PropertyId, reservation.CheckInAt, command.NewCheckOutAt,
                excludeReservationId: reservation.Id, cancellationToken);

            if (hasConflict)
            {
                throw new InvalidOperationException(
                    $"RescheduleReservationForLateCheckout: a real schedule conflict was re-detected for reservation " +
                    $"'{command.ReservationId}' at mutation time — Guest Operations' own eligibility read is TOCTOU-accepted, " +
                    "never a substitute for this transactional re-check; relies on Wolverine's own default error handling.");
            }

            var now = _timeProvider.GetUtcNow();
            reservation.Reschedule(reservation.CheckInAt, command.NewCheckOutAt, now);
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
                ChangedFields = ["check_out_at"],
                CheckInAt = reservation.CheckInAt,
                CheckOutAt = reservation.CheckOutAt,
            });

            _logger.LogInformation(
                "Reservation rescheduled for late checkout, tenant {TenantId} reservationId {ReservationId}",
                command.TenantId, reservation.Id);

            return true;
        }, cancellationToken);
}
