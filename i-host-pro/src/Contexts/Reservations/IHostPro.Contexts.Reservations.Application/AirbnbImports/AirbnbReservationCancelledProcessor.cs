using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.ExternalIntegrations.Contracts;
using IHostPro.Contexts.Reservations.Application.Reservations;
using IHostPro.Contexts.Reservations.Contracts;
using IHostPro.Contexts.Reservations.Domain;
using IHostPro.Contexts.Reservations.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace IHostPro.Contexts.Reservations.Application.AirbnbImports;

/// <summary>
/// Cancels the real <see cref="Reservation"/> matching an already-imported
/// Airbnb reservation (Fase 9, Checkpoint 3.2). Looks it up by
/// (<see cref="ReservationSource.Airbnb"/>,
/// <see cref="AirbnbReservationCancelled.ExternalReservationId"/>) — unknown
/// never creates a <see cref="Reservation"/> (CP3.2 mandate §22), already-
/// <see cref="Domain.Enums.ReservationStatus.Cancelled"/> is a no-op (never
/// re-throws <see cref="Reservation.Cancel"/>'s own guard). Publishes the
/// SAME <see cref="ReservationCancelled"/> a manual cancellation publishes —
/// this is the one Airbnb consumer where skipping the event would be a real
/// functional regression, not just cosmetic: Housekeeping's own automatic
/// cleaning-cancellation reaction depends on it (CP3.2 mandate §18's "same
/// behavior regardless of source" principle applied to cancellation).
/// </summary>
public sealed class AirbnbReservationCancelledProcessor : IIntegrationEventHandler<AirbnbReservationCancelled>
{
    private readonly IReservationReader _reader;
    private readonly IRepository<Reservation, Guid> _repository;
    private readonly IIntegrationEventCollector _eventCollector;
    private readonly IReservationsTransactionExecutor _transactionExecutor;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AirbnbReservationCancelledProcessor> _logger;

    public AirbnbReservationCancelledProcessor(
        IReservationReader reader,
        IRepository<Reservation, Guid> repository,
        IIntegrationEventCollector eventCollector,
        IReservationsTransactionExecutor transactionExecutor,
        TimeProvider timeProvider,
        ILogger<AirbnbReservationCancelledProcessor> logger)
    {
        _reader = reader;
        _repository = repository;
        _eventCollector = eventCollector;
        _transactionExecutor = transactionExecutor;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public Task HandleAsync(AirbnbReservationCancelled @event, CancellationToken cancellationToken) =>
        _transactionExecutor.ExecuteAsync(async () =>
        {
            var existingId = await _reader.GetIdByExternalIdentityAsync(
                ReservationSource.Airbnb, @event.ExternalReservationId, cancellationToken);

            if (existingId is null)
            {
                _logger.LogWarning(
                    "Airbnb cancellation ignored for tenant {TenantId} externalReservationId {ExternalReservationId}: {Result}",
                    @event.TenantId, @event.ExternalReservationId, "UnknownReservation");
                return true;
            }

            var reservation = await _repository.GetByIdAsync(existingId.Value, cancellationToken);
            if (reservation is null)
            {
                _logger.LogWarning(
                    "Airbnb cancellation ignored for tenant {TenantId} externalReservationId {ExternalReservationId}: {Result}",
                    @event.TenantId, @event.ExternalReservationId, "ReservationNoLongerExists");
                return true;
            }

            if (reservation.Status == ReservationStatus.Cancelled)
            {
                _logger.LogInformation(
                    "Airbnb cancellation no-op for tenant {TenantId} reservationId {ReservationId}: {Result}",
                    @event.TenantId, reservation.Id, "AlreadyCancelled");
                return true;
            }

            var now = _timeProvider.GetUtcNow();
            reservation.Cancel(now);
            _repository.Update(reservation);

            _eventCollector.Enqueue(new ReservationCancelled
            {
                TenantId = @event.TenantId,
                AggregateId = reservation.Id,
                AggregateType = "Reservation",
                CorrelationId = @event.CorrelationId,
                ActorType = "Integration",
                ReservationId = reservation.Id,
                PropertyId = reservation.PropertyId,
            });

            _logger.LogInformation(
                "Airbnb reservation cancelled for tenant {TenantId} reservationId {ReservationId}",
                @event.TenantId, reservation.Id);

            return true;
        }, cancellationToken);
}
