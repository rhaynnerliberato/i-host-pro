using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.Housekeeping.Contracts;
using IHostPro.Contexts.Housekeeping.Domain;

namespace IHostPro.Contexts.Housekeeping.Application.Cleanings;

/// <summary>
/// Creates the Cleaning associated with a newly created Reservation, in
/// reaction to Workflow Orchestration's <see cref="CreateCleaningForReservation"/>
/// command (Fase 8, Checkpoint 1 — ADR-018). Mirrors
/// <c>CreateCleaningCommandHandler</c>'s own division (projection reads
/// conclude, connections fully closed, before the write transaction opens),
/// with two additional guards this automated flow needs and the HTTP flow
/// does not:
///
/// 1. Idempotency (<see cref="ICleaningReader.ExistsAutomatedForReservationAsync"/>) —
///    a redelivered command must never create a second automated Cleaning.
///    Two layers, per ADR-018: this Application-level check, backed by a
///    database-level partial unique index as the real guarantee (a race
///    between two concurrent deliveries could both pass this check before
///    either commits — the index is what actually prevents the duplicate,
///    surfacing as a write failure the outbox/Wolverine redelivery already
///    tolerates).
/// 2. Best-effort cancellation guard (<see cref="IReservationReferenceProjection.IsCancelledAsync"/>) —
///    never a consistency guarantee, see ADR-018's own accepted-risk
///    section.
///
/// <see cref="Cleaning.CreatedByUserId"/> is always <c>null</c> here — this
/// flow has no authenticated actor, per ADR-018 (no seeded "system user").
/// <c>ScheduledAtUtc</c> is always <c>null</c> — deriving it from the
/// Reservation's checkout date is explicitly out of scope until Fase 10.
/// </summary>
public sealed class CreateCleaningForReservationCommandHandler : ICreateCleaningForReservationHandler
{
    private readonly IPropertyReferenceProjection _propertyProjection;
    private readonly IReservationReferenceProjection _reservationProjection;
    private readonly ICleaningReader _cleaningReader;
    private readonly IHousekeepingTransactionExecutor _executor;
    private readonly IRepository<Cleaning, Guid> _repository;
    private readonly IHousekeepingAuditWriter _auditWriter;
    private readonly IIntegrationEventCollector _eventCollector;
    private readonly TimeProvider _timeProvider;

    public CreateCleaningForReservationCommandHandler(
        IPropertyReferenceProjection propertyProjection,
        IReservationReferenceProjection reservationProjection,
        ICleaningReader cleaningReader,
        IHousekeepingTransactionExecutor executor,
        IRepository<Cleaning, Guid> repository,
        IHousekeepingAuditWriter auditWriter,
        IIntegrationEventCollector eventCollector,
        TimeProvider timeProvider)
    {
        _propertyProjection = propertyProjection;
        _reservationProjection = reservationProjection;
        _cleaningReader = cleaningReader;
        _executor = executor;
        _repository = repository;
        _auditWriter = auditWriter;
        _eventCollector = eventCollector;
        _timeProvider = timeProvider;
    }

    public async Task HandleAsync(CreateCleaningForReservation command, CancellationToken cancellationToken)
    {
        var alreadyCreated = await _cleaningReader.ExistsAutomatedForReservationAsync(
            command.TenantId, command.ReservationId, cancellationToken);

        if (alreadyCreated)
            return;

        var isCancelled = await _reservationProjection.IsCancelledAsync(
            command.TenantId, command.ReservationId, cancellationToken);

        if (isCancelled)
            return;

        var isKnownActiveProperty = await _propertyProjection.IsKnownActivePropertyAsync(
            command.TenantId, command.PropertyId, cancellationToken);

        if (!isKnownActiveProperty)
        {
            throw new InvalidOperationException(
                $"CreateCleaningForReservation: property '{command.PropertyId}' is not a known active property " +
                $"for tenant '{command.TenantId}' — relies on Wolverine's own default redelivery behavior to " +
                "recover from a transient Property Management projection lag; no custom retry policy introduced.");
        }

        await _executor.ExecuteAsync(async () =>
        {
            var now = _timeProvider.GetUtcNow();
            var cleaningId = Guid.NewGuid();

            var cleaning = Cleaning.Create(
                cleaningId, command.TenantId, command.PropertyId, command.ReservationId,
                createdByUserId: null, now, scheduledAtUtc: null);

            _repository.Add(cleaning);

            _auditWriter.Record(CleaningAuditEntry.Create(
                Guid.NewGuid(), command.TenantId, actorUserId: null, "Cleaning", cleaningId,
                "cleaning_created_by_workflow", changedFields: [], now));

            _eventCollector.Enqueue(new CleaningCreated
            {
                TenantId = command.TenantId,
                AggregateId = cleaningId,
                AggregateType = "Cleaning",
                CorrelationId = command.CorrelationId,
                CausationId = command.CausationId,
                ActorType = "System",
                ActorId = null,
                CleaningId = cleaningId,
                PropertyId = command.PropertyId,
                ReservationId = command.ReservationId,
                Status = CleaningStatusCodeMapper.ToCode(cleaning.Status),
                ScheduledAtUtc = cleaning.ScheduledAtUtc,
            });

            return true;
        }, cancellationToken);
    }
}
