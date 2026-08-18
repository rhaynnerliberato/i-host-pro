using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.Housekeeping.Contracts;
using IHostPro.Contexts.Housekeeping.Domain;

namespace IHostPro.Contexts.Housekeeping.Application.Cleanings;

/// <summary>
/// Creates the Cleaning associated with a newly created Reservation, in
/// reaction to Workflow Orchestration's <see cref="CreateCleaningForReservation"/>
/// command (Fase 8, Checkpoint 1 — ADR-018; cancellation-safety corrected at
/// Checkpoint 1.1). Mirrors <c>CreateCleaningCommandHandler</c>'s own
/// division for the property check (projection read concludes, connection
/// fully closed, before the write transaction opens) — but, unlike that
/// flow, everything from lock acquisition onward runs INSIDE the write
/// transaction, on purpose (see below).
///
/// <see cref="Cleaning.CreatedByUserId"/> is always <c>null</c> here — this
/// flow has no authenticated actor, per ADR-018 (no seeded "system user").
/// <c>ScheduledAtUtc</c> is always <c>null</c> — deriving it from the
/// Reservation's checkout date is explicitly out of scope until Fase 10.
///
/// Cancellation safety (Fase 8, Checkpoint 1.1 — corrects the CP1 best-effort
/// guard rejected at corrective review): everything from
/// <see cref="IReservationCancellationGuard.AcquireLockAsync"/> onward runs
/// inside the SAME write transaction, in this exact order:
/// 1. acquire the per-(tenantId, reservationId) advisory lock — the real
///    serialization point, shared with <c>ReservationProjectionAndCancellationReaction</c>'s
///    own <c>ReservationCreated</c>/<c>ReservationCancelled</c> reactions;
/// 2. <see cref="IReservationReferenceProjection.EnsureExistsAsync"/> —
///    materializes the local reference even if this command legitimately
///    arrives before Housekeeping's own <c>ReservationCreated</c> reaction
///    has (no synchronous read back to Reservations, no invented business
///    data — only the (tenantId, reservationId) identity this command
///    already carries);
/// 3. <see cref="IReservationReferenceProjection.IsCancelledAsync"/> — now a
///    DETERMINISTIC check (never best-effort): under the lock, no
///    concurrently racing cancellation reaction can still be in flight;
/// 4. the idempotency check (<see cref="ICleaningReader.ExistsAutomatedForReservationAsync"/>) —
///    also now lock-protected, so a redelivered command can never create a
///    second automated Cleaning even under genuine concurrent delivery; the
///    database-level partial unique index remains as defense in depth;
/// 5. create the Cleaning, only if every guard above allowed it.
/// </summary>
public sealed class CreateCleaningForReservationCommandHandler : ICreateCleaningForReservationHandler
{
    private readonly IPropertyReferenceProjection _propertyProjection;
    private readonly IReservationReferenceProjection _reservationProjection;
    private readonly IReservationCancellationGuard _cancellationGuard;
    private readonly ICleaningReader _cleaningReader;
    private readonly IHousekeepingTransactionExecutor _executor;
    private readonly IRepository<Cleaning, Guid> _repository;
    private readonly IHousekeepingAuditWriter _auditWriter;
    private readonly IIntegrationEventCollector _eventCollector;
    private readonly TimeProvider _timeProvider;

    public CreateCleaningForReservationCommandHandler(
        IPropertyReferenceProjection propertyProjection,
        IReservationReferenceProjection reservationProjection,
        IReservationCancellationGuard cancellationGuard,
        ICleaningReader cleaningReader,
        IHousekeepingTransactionExecutor executor,
        IRepository<Cleaning, Guid> repository,
        IHousekeepingAuditWriter auditWriter,
        IIntegrationEventCollector eventCollector,
        TimeProvider timeProvider)
    {
        _propertyProjection = propertyProjection;
        _reservationProjection = reservationProjection;
        _cancellationGuard = cancellationGuard;
        _cleaningReader = cleaningReader;
        _executor = executor;
        _repository = repository;
        _auditWriter = auditWriter;
        _eventCollector = eventCollector;
        _timeProvider = timeProvider;
    }

    public async Task HandleAsync(CreateCleaningForReservation command, CancellationToken cancellationToken)
    {
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
            await _cancellationGuard.AcquireLockAsync(command.TenantId, command.ReservationId, cancellationToken);

            await _reservationProjection.EnsureExistsAsync(command.TenantId, command.ReservationId, cancellationToken);

            var isCancelled = await _reservationProjection.IsCancelledAsync(
                command.TenantId, command.ReservationId, cancellationToken);

            if (isCancelled)
                return true;

            var alreadyCreated = await _cleaningReader.ExistsAutomatedForReservationAsync(
                command.TenantId, command.ReservationId, cancellationToken);

            if (alreadyCreated)
                return true;

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
