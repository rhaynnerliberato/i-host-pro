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
/// Materializes a real <see cref="Reservation"/> from an Airbnb-imported
/// reservation (Fase 9, Checkpoint 3.2 — "Airbnb Deterministic Foundation";
/// CP3.1 Decision Gate item 20). Idempotent on
/// (<see cref="ReservationSource.Airbnb"/>, <see cref="AirbnbReservationImported.ExternalReservationId"/>)
/// — a redelivery of an already-imported reservation is a no-op, never a
/// duplicate <see cref="Reservation"/> (the database's own partial unique
/// index is the authoritative guard; this lookup is the fast, ordinary
/// path). Publishes the SAME <see cref="ReservationCreated"/> a manual
/// reservation publishes — no new consumer wiring, no
/// <c>AddStickyHandler</c> change needed (ADR-020's own "single discovered
/// handler per queue" default is unaffected: this is a new PRODUCER of an
/// existing event, not a new consumer of one). Never calls Communication
/// directly (CP3.2 mandate §20) — the consent boundary lives entirely inside
/// Communication's own consumer, keyed off <see cref="ReservationCreated.Source"/>.
/// </summary>
public sealed class AirbnbReservationImportedProcessor : IIntegrationEventHandler<AirbnbReservationImported>
{
    private readonly IReservationReader _reader;
    private readonly IRepository<Reservation, Guid> _repository;
    private readonly IIntegrationEventCollector _eventCollector;
    private readonly IReservationsTransactionExecutor _transactionExecutor;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AirbnbReservationImportedProcessor> _logger;

    public AirbnbReservationImportedProcessor(
        IReservationReader reader,
        IRepository<Reservation, Guid> repository,
        IIntegrationEventCollector eventCollector,
        IReservationsTransactionExecutor transactionExecutor,
        TimeProvider timeProvider,
        ILogger<AirbnbReservationImportedProcessor> logger)
    {
        _reader = reader;
        _repository = repository;
        _eventCollector = eventCollector;
        _transactionExecutor = transactionExecutor;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public Task HandleAsync(AirbnbReservationImported @event, CancellationToken cancellationToken) =>
        _transactionExecutor.ExecuteAsync(async () =>
        {
            var existingId = await _reader.GetIdByExternalIdentityAsync(
                ReservationSource.Airbnb, @event.ExternalReservationId, cancellationToken);

            if (existingId is not null)
            {
                _logger.LogInformation(
                    "Airbnb import skipped for tenant {TenantId} externalReservationId {ExternalReservationId}: {Result}",
                    @event.TenantId, @event.ExternalReservationId, "AlreadyImported");
                return true;
            }

            var now = _timeProvider.GetUtcNow();
            var reservation = Reservation.CreateImported(
                Guid.NewGuid(), @event.TenantId, @event.PropertyId, @event.GuestName, guestPhone: null,
                @event.CheckInAt, @event.CheckOutAt, @event.GuestCount, @event.ExternalReservationId, now);

            _repository.Add(reservation);

            _eventCollector.Enqueue(new ReservationCreated
            {
                TenantId = @event.TenantId,
                AggregateId = reservation.Id,
                AggregateType = "Reservation",
                CorrelationId = @event.CorrelationId,
                ActorType = "Integration",
                ReservationId = reservation.Id,
                PropertyId = reservation.PropertyId,
                Status = ReservationStatusCodeMapper.ToCode(reservation.Status),
                CheckInAt = reservation.CheckInAt,
                CheckOutAt = reservation.CheckOutAt,
                Source = ReservationSourceCodeMapper.ToCode(reservation.Source),
            });

            _logger.LogInformation(
                "Airbnb reservation imported for tenant {TenantId} reservationId {ReservationId} externalReservationId {ExternalReservationId}",
                @event.TenantId, reservation.Id, @event.ExternalReservationId);

            return true;
        }, cancellationToken);
}
