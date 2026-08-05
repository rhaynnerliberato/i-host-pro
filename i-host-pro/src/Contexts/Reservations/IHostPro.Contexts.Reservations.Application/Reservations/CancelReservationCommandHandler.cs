using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.Reservations.Application.Errors;
using IHostPro.Contexts.Reservations.Contracts;
using IHostPro.Contexts.Reservations.Domain;

namespace IHostPro.Contexts.Reservations.Application.Reservations;

/// <summary>
/// <c>Confirmed</c> → <c>Cancelled</c> (Fase 3, Incremento 1 plan, item 8).
/// The handler consults the current status BEFORE calling
/// <see cref="Reservation.Cancel"/> so it can translate an already-cancelled
/// reservation into the precise stable error code — mirrors
/// <c>ActivatePropertyCommandHandler</c>'s own division of responsibility
/// with <c>Property.Activate</c>.
/// </summary>
public sealed class CancelReservationCommandHandler : ICommandHandler<CancelReservationCommand, ReservationResult>
{
    private static readonly Error ReservationNotFoundError = new(
        ReservationsErrorCodes.ReservationNotFound, ReservationsErrorCodes.ReservationNotFound);
    private static readonly Error ReservationAlreadyCancelledError = new(
        ReservationsErrorCodes.ReservationAlreadyCancelled, ReservationsErrorCodes.ReservationAlreadyCancelled);

    private readonly ICancelReservationExecutor _executor;
    private readonly IRepository<Reservation, Guid> _repository;
    private readonly IReservationAuditWriter _auditWriter;
    private readonly IIntegrationEventCollector _eventCollector;
    private readonly TimeProvider _timeProvider;

    public CancelReservationCommandHandler(
        ICancelReservationExecutor executor,
        IRepository<Reservation, Guid> repository,
        IReservationAuditWriter auditWriter,
        IIntegrationEventCollector eventCollector,
        TimeProvider timeProvider)
    {
        _executor = executor;
        _repository = repository;
        _auditWriter = auditWriter;
        _eventCollector = eventCollector;
        _timeProvider = timeProvider;
    }

    public async ValueTask<Result<ReservationResult>> Handle(CancelReservationCommand command, CancellationToken cancellationToken) =>
        await _executor.ExecuteAsync(async () =>
        {
            var reservation = await _repository.GetByIdAsync(command.ReservationId, cancellationToken);
            if (reservation is null)
                return Result.Failure<ReservationResult>(ReservationNotFoundError);

            if (reservation.Status == Domain.Enums.ReservationStatus.Cancelled)
                return Result.Failure<ReservationResult>(ReservationAlreadyCancelledError);

            var now = _timeProvider.GetUtcNow();
            reservation.Cancel(now);

            var correlationId = Guid.NewGuid();

            _auditWriter.Record(ReservationAuditEntry.Create(
                Guid.NewGuid(), command.TenantId, command.ActorId, "Reservation", command.ReservationId,
                "reservation_cancelled", ["status"], now));

            _eventCollector.Enqueue(new ReservationCancelled
            {
                TenantId = command.TenantId,
                AggregateId = command.ReservationId,
                AggregateType = "Reservation",
                CorrelationId = correlationId,
                ActorType = "User",
                ActorId = command.ActorId.ToString(),
                ReservationId = command.ReservationId,
                PropertyId = reservation.PropertyId,
            });

            return Result.Success(CreateReservationCommandHandler.ToResult(reservation));
        }, cancellationToken);
}
