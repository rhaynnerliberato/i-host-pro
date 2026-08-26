using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.PropertyManagement.Contracts;
using IHostPro.Contexts.Reservations.Application.Errors;
using IHostPro.Contexts.Reservations.Contracts;
using IHostPro.Contexts.Reservations.Domain;

namespace IHostPro.Contexts.Reservations.Application.Reservations;

/// <summary>
/// Creates a reservation, born <c>Confirmed</c> (Fase 3, Incremento 1 plan,
/// item 6/7). Step 1-2 (property eligibility) run BEFORE this context's own
/// write transaction opens — mirrors <c>LinkPropertyOwnerCommandHandler</c>'s
/// own division exactly: the read against Property Management concludes,
/// its own independent connection fully closed, before
/// <see cref="ICreateReservationExecutor"/> ever opens this context's
/// transaction (no distributed transaction, no long-held write transaction
/// across the cross-context call). The resulting small TOCTOU window (the
/// property's status/capacity could change between the eligibility check and
/// the commit) is accepted, same as Ownership's own precedent: the
/// reservation still confirms; a property deactivated moments later simply
/// keeps its already-confirmed reservations untouched (Fase 3, Incremento 1
/// plan, item 6).
///
/// Step 3+ (date-conflict check, insert) run inside the transaction
/// <see cref="ICreateReservationExecutor"/> opens, under
/// <see cref="IReservationConflictGuard"/>'s advisory lock — never calls
/// <c>SaveChangesAsync</c> itself.
/// </summary>
public sealed class CreateReservationCommandHandler : ICommandHandler<CreateReservationCommand, ReservationResult>
{
    private static readonly Error PropertyNotFoundError = new(
        ReservationsErrorCodes.PropertyNotFound, ReservationsErrorCodes.PropertyNotFound);
    private static readonly Error PropertyNotActiveError = new(
        ReservationsErrorCodes.PropertyNotActive, ReservationsErrorCodes.PropertyNotActive);
    private static readonly Error PropertyCapacityExceededError = new(
        ReservationsErrorCodes.PropertyCapacityExceeded, ReservationsErrorCodes.PropertyCapacityExceeded);
    private static readonly Error ReservationDateConflictError = new(
        ReservationsErrorCodes.ReservationDateConflict, ReservationsErrorCodes.ReservationDateConflict);
    private static readonly Error GuestNameInvalidError = new("guest_name_invalid", "guest_name_invalid");
    private static readonly Error ScheduleInvalidError = new("reservation_schedule_invalid", "reservation_schedule_invalid");

    private readonly IPropertyReservationEligibilityReader _eligibilityReader;
    private readonly ICreateReservationExecutor _executor;
    private readonly IReservationConflictGuard _conflictGuard;
    private readonly IRepository<Reservation, Guid> _repository;
    private readonly IReservationAuditWriter _auditWriter;
    private readonly IIntegrationEventCollector _eventCollector;
    private readonly TimeProvider _timeProvider;

    public CreateReservationCommandHandler(
        IPropertyReservationEligibilityReader eligibilityReader,
        ICreateReservationExecutor executor,
        IReservationConflictGuard conflictGuard,
        IRepository<Reservation, Guid> repository,
        IReservationAuditWriter auditWriter,
        IIntegrationEventCollector eventCollector,
        TimeProvider timeProvider)
    {
        _eligibilityReader = eligibilityReader;
        _executor = executor;
        _conflictGuard = conflictGuard;
        _repository = repository;
        _auditWriter = auditWriter;
        _eventCollector = eventCollector;
        _timeProvider = timeProvider;
    }

    public async ValueTask<Result<ReservationResult>> Handle(CreateReservationCommand command, CancellationToken cancellationToken)
    {
        var eligibility = await _eligibilityReader.GetAsync(command.TenantId, command.PropertyId, cancellationToken);

        if (eligibility is null)
            return Result.Failure<ReservationResult>(PropertyNotFoundError);

        if (!eligibility.IsActive)
            return Result.Failure<ReservationResult>(PropertyNotActiveError);

        if (command.GuestCount > eligibility.Capacity)
            return Result.Failure<ReservationResult>(PropertyCapacityExceededError);

        return await _executor.ExecuteAsync(async () =>
        {
            await _conflictGuard.AcquirePropertyLockAsync(command.TenantId, command.PropertyId, cancellationToken);

            var hasConflict = await _conflictGuard.HasConflictingReservationAsync(
                command.TenantId, command.PropertyId, command.CheckInAt, command.CheckOutAt,
                excludeReservationId: null, cancellationToken);

            if (hasConflict)
                return Result.Failure<ReservationResult>(ReservationDateConflictError);

            var now = _timeProvider.GetUtcNow();
            var reservationId = Guid.NewGuid();

            Reservation reservation;
            try
            {
                reservation = Reservation.Create(
                    reservationId, command.TenantId, command.PropertyId, command.GuestName, command.GuestPhone,
                    command.CheckInAt, command.CheckOutAt, command.GuestCount, now);
            }
            catch (ArgumentException ex) when (ex.ParamName is nameof(command.CheckOutAt))
            {
                return Result.Failure<ReservationResult>(ScheduleInvalidError);
            }
            catch (ArgumentException)
            {
                return Result.Failure<ReservationResult>(GuestNameInvalidError);
            }

            _repository.Add(reservation);

            var correlationId = Guid.NewGuid();

            _auditWriter.Record(ReservationAuditEntry.Create(
                Guid.NewGuid(), command.TenantId, command.ActorId, "Reservation", reservationId,
                "reservation_created", changedFields: [], now));

            _eventCollector.Enqueue(new ReservationCreated
            {
                TenantId = command.TenantId,
                AggregateId = reservationId,
                AggregateType = "Reservation",
                CorrelationId = correlationId,
                ActorType = "User",
                ActorId = command.ActorId.ToString(),
                ReservationId = reservationId,
                PropertyId = command.PropertyId,
                Status = ReservationStatusCodeMapper.ToCode(reservation.Status),
                CheckInAt = reservation.CheckInAt,
                CheckOutAt = reservation.CheckOutAt,
                Source = ReservationSourceCodeMapper.ToCode(reservation.Source),
            });

            return Result.Success(ToResult(reservation));
        }, cancellationToken);
    }

    internal static ReservationResult ToResult(Reservation reservation) => new(
        reservation.Id,
        reservation.PropertyId,
        reservation.GuestName,
        reservation.GuestPhone,
        reservation.CheckInAt,
        reservation.CheckOutAt,
        reservation.GuestCount,
        ReservationStatusCodeMapper.ToCode(reservation.Status),
        reservation.CreatedAt,
        reservation.UpdatedAt);
}
