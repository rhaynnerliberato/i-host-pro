using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.Reservations.Contracts;
using IHostPro.Contexts.Reservations.Domain;
using IHostPro.Contexts.Reservations.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace IHostPro.Contexts.Reservations.Application.Reservations;

/// <summary>
/// Reacts to Workflow Orchestration's <see cref="CloseReservation"/> command
/// (Fase 10, Checkpoint 1 — Guest Operations Foundation), implementing the
/// user-approved closure semantics exactly:
/// <see cref="ReservationStatus.Confirmed"/> → <see cref="ReservationStatus.Closed"/>,
/// publishing <see cref="ReservationClosed"/> exactly once;
/// <see cref="ReservationStatus.Closed"/> is a silent idempotent no-op (never
/// re-throws <see cref="Reservation.Close"/>'s own guard, never republishes);
/// <see cref="ReservationStatus.Cancelled"/> is an invariant violation — this
/// command is produced by an internal, well-ordered chain (Guest Operations →
/// Workflow → Reservations), never an external/unordered event source like
/// Airbnb's own imports, so this case throws
/// <see cref="ReservationCancelledCannotBeClosedException"/> and relies
/// exclusively on Wolverine's default single-attempt/dead-letter handling —
/// no custom retry policy, no restoration, no <see cref="ReservationClosed"/>.
/// A missing reservation is a separate, generic anomaly (the internal sender
/// should always know a real reservation id) and throws a plain
/// <see cref="InvalidOperationException"/>, same rationale.
/// </summary>
public sealed class CloseReservationCommandHandler : ICloseReservationHandler
{
    private readonly IRepository<Reservation, Guid> _repository;
    private readonly IIntegrationEventCollector _eventCollector;
    private readonly IReservationsTransactionExecutor _transactionExecutor;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<CloseReservationCommandHandler> _logger;

    public CloseReservationCommandHandler(
        IRepository<Reservation, Guid> repository,
        IIntegrationEventCollector eventCollector,
        IReservationsTransactionExecutor transactionExecutor,
        TimeProvider timeProvider,
        ILogger<CloseReservationCommandHandler> logger)
    {
        _repository = repository;
        _eventCollector = eventCollector;
        _transactionExecutor = transactionExecutor;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public Task HandleAsync(CloseReservation command, CancellationToken cancellationToken) =>
        _transactionExecutor.ExecuteAsync(async () =>
        {
            var reservation = await _repository.GetByIdAsync(command.ReservationId, cancellationToken);

            if (reservation is null)
            {
                throw new InvalidOperationException(
                    $"CloseReservation: reservation '{command.ReservationId}' not found for tenant " +
                    $"'{command.TenantId}' — relies on Wolverine's own default redelivery behavior; " +
                    "no custom retry policy introduced.");
            }

            if (reservation.Status == ReservationStatus.Closed)
            {
                _logger.LogInformation(
                    "CloseReservation no-op for tenant {TenantId} reservationId {ReservationId}: {Result}",
                    command.TenantId, reservation.Id, "AlreadyClosed");
                return true;
            }

            if (reservation.Status == ReservationStatus.Cancelled)
            {
                throw new ReservationCancelledCannotBeClosedException(
                    $"CloseReservation: reservation '{command.ReservationId}' is Cancelled and cannot be " +
                    "closed — invariant violation in the internal Guest Operations -> Workflow -> Reservations " +
                    "checkout chain; relies on Wolverine's own default error handling, no custom retry policy.");
            }

            var now = _timeProvider.GetUtcNow();
            reservation.Close(now);
            _repository.Update(reservation);

            _eventCollector.Enqueue(new ReservationClosed
            {
                TenantId = command.TenantId,
                AggregateId = reservation.Id,
                AggregateType = "Reservation",
                CorrelationId = command.CorrelationId,
                CausationId = command.CausationId,
                ActorType = "System",
                ReservationId = reservation.Id,
                PropertyId = reservation.PropertyId,
            });

            _logger.LogInformation(
                "Reservation closed for tenant {TenantId} reservationId {ReservationId}",
                command.TenantId, reservation.Id);

            return true;
        }, cancellationToken);
}
