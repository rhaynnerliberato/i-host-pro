using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.Housekeeping.Application.Errors;
using IHostPro.Contexts.Housekeeping.Contracts;
using IHostPro.Contexts.Housekeeping.Domain;

namespace IHostPro.Contexts.Housekeeping.Application.Cleanings;

/// <summary>
/// Creates a cleaning, born <c>Pending</c> (Fase 6, Incremento 1). Property
/// and (optional) reservation reference checks run BEFORE this context's
/// own write transaction opens — mirrors <c>CreateReservationCommandHandler</c>'s
/// own division: both projection reads conclude, their connections fully
/// closed, before <see cref="IHousekeepingTransactionExecutor"/> ever opens
/// this context's transaction.
/// </summary>
public sealed class CreateCleaningCommandHandler : ICommandHandler<CreateCleaningCommand, CleaningResult>
{
    private static readonly Error PropertyNotFoundError = new(
        HousekeepingErrorCodes.PropertyNotFound, HousekeepingErrorCodes.PropertyNotFound);
    private static readonly Error ReservationReferenceNotAvailableError = new(
        HousekeepingErrorCodes.ReservationReferenceNotAvailable, HousekeepingErrorCodes.ReservationReferenceNotAvailable);

    private readonly IPropertyReferenceProjection _propertyProjection;
    private readonly IReservationReferenceProjection _reservationProjection;
    private readonly IHousekeepingTransactionExecutor _executor;
    private readonly IRepository<Cleaning, Guid> _repository;
    private readonly IHousekeepingAuditWriter _auditWriter;
    private readonly IIntegrationEventCollector _eventCollector;
    private readonly TimeProvider _timeProvider;

    public CreateCleaningCommandHandler(
        IPropertyReferenceProjection propertyProjection,
        IReservationReferenceProjection reservationProjection,
        IHousekeepingTransactionExecutor executor,
        IRepository<Cleaning, Guid> repository,
        IHousekeepingAuditWriter auditWriter,
        IIntegrationEventCollector eventCollector,
        TimeProvider timeProvider)
    {
        _propertyProjection = propertyProjection;
        _reservationProjection = reservationProjection;
        _executor = executor;
        _repository = repository;
        _auditWriter = auditWriter;
        _eventCollector = eventCollector;
        _timeProvider = timeProvider;
    }

    public async ValueTask<Result<CleaningResult>> Handle(CreateCleaningCommand command, CancellationToken cancellationToken)
    {
        var isKnownActiveProperty = await _propertyProjection.IsKnownActivePropertyAsync(
            command.TenantId, command.PropertyId, cancellationToken);

        if (!isKnownActiveProperty)
            return Result.Failure<CleaningResult>(PropertyNotFoundError);

        if (command.ReservationId is { } reservationId)
        {
            var reservationExists = await _reservationProjection.ExistsAsync(command.TenantId, reservationId, cancellationToken);
            if (!reservationExists)
                return Result.Failure<CleaningResult>(ReservationReferenceNotAvailableError);
        }

        return await _executor.ExecuteAsync(async () =>
        {
            var now = _timeProvider.GetUtcNow();
            var cleaningId = Guid.NewGuid();

            var cleaning = Cleaning.Create(
                cleaningId, command.TenantId, command.PropertyId, command.ReservationId, command.ActorId, now,
                command.ScheduledAtUtc);

            _repository.Add(cleaning);

            var correlationId = Guid.NewGuid();

            _auditWriter.Record(CleaningAuditEntry.Create(
                Guid.NewGuid(), command.TenantId, command.ActorId, "Cleaning", cleaningId,
                "cleaning_created", changedFields: [], now));

            _eventCollector.Enqueue(new CleaningCreated
            {
                TenantId = command.TenantId,
                AggregateId = cleaningId,
                AggregateType = "Cleaning",
                CorrelationId = correlationId,
                ActorType = "User",
                ActorId = command.ActorId.ToString(),
                CleaningId = cleaningId,
                PropertyId = command.PropertyId,
                ReservationId = command.ReservationId,
                Status = CleaningStatusCodeMapper.ToCode(cleaning.Status),
            });

            return Result.Success(ToResult(cleaning));
        }, cancellationToken);
    }

    internal static CleaningResult ToResult(Cleaning cleaning) => new(
        cleaning.Id,
        cleaning.PropertyId,
        cleaning.ReservationId,
        cleaning.AssignedHousekeeperUserId,
        CleaningStatusCodeMapper.ToCode(cleaning.Status),
        cleaning.CreatedByUserId,
        cleaning.CreatedAtUtc,
        cleaning.ScheduledAtUtc,
        cleaning.StartedAtUtc,
        cleaning.InspectionStartedAtUtc,
        cleaning.CompletedAtUtc,
        cleaning.CancelledAtUtc);
}
