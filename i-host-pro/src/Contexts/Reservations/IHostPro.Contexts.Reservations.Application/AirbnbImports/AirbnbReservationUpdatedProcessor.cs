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
/// Applies an already-imported Airbnb reservation's change to the matching
/// real <see cref="Reservation"/> (Fase 9, Checkpoint 3.2). Looks the
/// reservation up by (<see cref="ReservationSource.Airbnb"/>,
/// <see cref="AirbnbReservationUpdated.ExternalReservationId"/>) — an
/// UNKNOWN external id (no prior import ever reached this consumer) is
/// logged and ignored, never auto-created (CP3.2 mandate §21: no precedent
/// authorizes inventing that policy). Reuses the exact same mutators
/// <c>UpdateReservationCommandHandler</c> already calls for a manual PATCH,
/// diffed the same way; publishes <see cref="ReservationUpdated"/> only when
/// something actually changed, exactly like the manual path, so Dashboard's
/// own projection (its only in-process consumer) never goes stale for an
/// Airbnb-sourced reschedule either.
/// </summary>
public sealed class AirbnbReservationUpdatedProcessor : IIntegrationEventHandler<AirbnbReservationUpdated>
{
    private readonly IReservationReader _reader;
    private readonly IRepository<Reservation, Guid> _repository;
    private readonly IIntegrationEventCollector _eventCollector;
    private readonly IReservationsTransactionExecutor _transactionExecutor;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AirbnbReservationUpdatedProcessor> _logger;

    public AirbnbReservationUpdatedProcessor(
        IReservationReader reader,
        IRepository<Reservation, Guid> repository,
        IIntegrationEventCollector eventCollector,
        IReservationsTransactionExecutor transactionExecutor,
        TimeProvider timeProvider,
        ILogger<AirbnbReservationUpdatedProcessor> logger)
    {
        _reader = reader;
        _repository = repository;
        _eventCollector = eventCollector;
        _transactionExecutor = transactionExecutor;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public Task HandleAsync(AirbnbReservationUpdated @event, CancellationToken cancellationToken) =>
        _transactionExecutor.ExecuteAsync(async () =>
        {
            var existingId = await _reader.GetIdByExternalIdentityAsync(
                ReservationSource.Airbnb, @event.ExternalReservationId, cancellationToken);

            if (existingId is null)
            {
                _logger.LogWarning(
                    "Airbnb update ignored for tenant {TenantId} externalReservationId {ExternalReservationId}: {Result}",
                    @event.TenantId, @event.ExternalReservationId, "UnknownReservation");
                return true;
            }

            var reservation = await _repository.GetByIdAsync(existingId.Value, cancellationToken);
            if (reservation is null)
            {
                _logger.LogWarning(
                    "Airbnb update ignored for tenant {TenantId} externalReservationId {ExternalReservationId}: {Result}",
                    @event.TenantId, @event.ExternalReservationId, "ReservationNoLongerExists");
                return true;
            }

            var now = _timeProvider.GetUtcNow();
            var changedFields = new List<string>();

            if (reservation.PropertyId != @event.PropertyId)
            {
                reservation.ChangeProperty(@event.PropertyId, now);
                changedFields.Add("property_id");
            }

            if (!string.Equals(reservation.GuestName, @event.GuestName, StringComparison.Ordinal))
            {
                reservation.ChangeGuestName(@event.GuestName, now);
                changedFields.Add("guest_name");
            }

            var checkInChanged = reservation.CheckInAt != @event.CheckInAt;
            var checkOutChanged = reservation.CheckOutAt != @event.CheckOutAt;
            if (checkInChanged || checkOutChanged)
            {
                reservation.Reschedule(@event.CheckInAt, @event.CheckOutAt, now);
                if (checkInChanged)
                    changedFields.Add("check_in_at");
                if (checkOutChanged)
                    changedFields.Add("check_out_at");
            }

            if (reservation.GuestCount != @event.GuestCount)
            {
                reservation.ChangeGuestCount(@event.GuestCount, now);
                changedFields.Add("guest_count");
            }

            if (changedFields.Count == 0)
            {
                _logger.LogInformation(
                    "Airbnb update no-op for tenant {TenantId} reservationId {ReservationId}: {Result}",
                    @event.TenantId, reservation.Id, "NoChanges");
                return true;
            }

            _repository.Update(reservation);

            _eventCollector.Enqueue(new ReservationUpdated
            {
                TenantId = @event.TenantId,
                AggregateId = reservation.Id,
                AggregateType = "Reservation",
                CorrelationId = @event.CorrelationId,
                ActorType = "Integration",
                ReservationId = reservation.Id,
                ChangedFields = changedFields,
                CheckInAt = reservation.CheckInAt,
                CheckOutAt = reservation.CheckOutAt,
            });

            _logger.LogInformation(
                "Airbnb reservation updated for tenant {TenantId} reservationId {ReservationId} fields {ChangedFields}",
                @event.TenantId, reservation.Id, string.Join(",", changedFields));

            return true;
        }, cancellationToken);
}
