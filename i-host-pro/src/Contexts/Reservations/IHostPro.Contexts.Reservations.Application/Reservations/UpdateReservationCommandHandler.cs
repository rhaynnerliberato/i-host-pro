using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.PropertyManagement.Contracts;
using IHostPro.Contexts.Reservations.Application.Errors;
using IHostPro.Contexts.Reservations.Contracts;
using IHostPro.Contexts.Reservations.Domain;
using IHostPro.Contexts.Reservations.Domain.Enums;

namespace IHostPro.Contexts.Reservations.Application.Reservations;

/// <summary>
/// Updates a reservation's property, guest name, guest phone, check-in,
/// check-out and/or guest count, idempotently. Fase 3 §3 transactional-order
/// correction — steps, in order:
///
/// 1-3. A tenant-aware, RLS-protected, read-only snapshot
/// (<see cref="IReservationReader.GetUpdateSnapshotAsync"/>) — no explicit
/// transaction, so its connection returns to the pool as soon as it
/// completes.
/// 4. Prospective PropertyId/schedule/guest count, presence-aware, computed
/// from the snapshot and the PATCH.
/// 5-6. Property Management eligibility (<see cref="IPropertyReservationEligibilityReader"/>,
/// ADR-014) — concludes and its connection fully closes BEFORE this
/// context's write transaction opens, mirroring <see cref="CreateReservationCommandHandler"/>/
/// Property Ownership's own precedent. No transaction of two contexts is
/// ever open at once.
/// 7. This context's write transaction opens (<see cref="IUpdateReservationExecutor"/>).
/// 8. The tracked aggregate is re-read.
/// 9-10. The row's CURRENT <c>xmin</c> (<see cref="IReservationReader.GetCurrentXminAsync"/>)
/// must still equal the snapshot's — otherwise every decision made from the
/// snapshot (final PropertyId, eligibility, capacity) may already be stale,
/// and this returns <see cref="ReservationsErrorCodes.ReservationConcurrencyConflict"/>
/// immediately, with no automatic retry. This is a SEPARATE, earlier check
/// than <see cref="IUpdateReservationExecutor"/>'s own <c>DbUpdateConcurrencyException</c>
/// translation (which still guards the smaller window between this re-read
/// and the eventual <c>SaveChanges</c>, unchanged).
/// 11-12. Advisory lock + date-conflict check, exactly as before.
/// 13-14. Aggregate/audit/event persisted; the executor commits.
///
/// A <c>Cancelled</c> reservation rejects every PATCH outright — including a
/// values-already-match no-op — before any diffing (mirrors Property
/// Management's <c>ArchivedPropertyCannotBeModified</c> precedent).
/// </summary>
public sealed class UpdateReservationCommandHandler : ICommandHandler<UpdateReservationCommand, ReservationResult>
{
    private static readonly Error ReservationNotFoundError = new(
        ReservationsErrorCodes.ReservationNotFound, ReservationsErrorCodes.ReservationNotFound);
    private static readonly Error NoChangesProvidedError = new(
        ReservationsErrorCodes.NoChangesProvided, ReservationsErrorCodes.NoChangesProvided);
    private static readonly Error CancelledReservationCannotBeModifiedError = new(
        ReservationsErrorCodes.CancelledReservationCannotBeModified, ReservationsErrorCodes.CancelledReservationCannotBeModified);
    private static readonly Error PropertyNotFoundError = new(
        ReservationsErrorCodes.PropertyNotFound, ReservationsErrorCodes.PropertyNotFound);
    private static readonly Error PropertyNotActiveError = new(
        ReservationsErrorCodes.PropertyNotActive, ReservationsErrorCodes.PropertyNotActive);
    private static readonly Error PropertyCapacityExceededError = new(
        ReservationsErrorCodes.PropertyCapacityExceeded, ReservationsErrorCodes.PropertyCapacityExceeded);
    private static readonly Error ReservationDateConflictError = new(
        ReservationsErrorCodes.ReservationDateConflict, ReservationsErrorCodes.ReservationDateConflict);
    private static readonly Error ReservationConcurrencyConflictError = new(
        ReservationsErrorCodes.ReservationConcurrencyConflict, ReservationsErrorCodes.ReservationConcurrencyConflict);
    private static readonly Error ScheduleInvalidError = new("reservation_schedule_invalid", "reservation_schedule_invalid");

    private readonly IReservationReader _reader;
    private readonly IPropertyReservationEligibilityReader _eligibilityReader;
    private readonly IUpdateReservationExecutor _executor;
    private readonly IReservationConflictGuard _conflictGuard;
    private readonly IRepository<Reservation, Guid> _repository;
    private readonly IReservationAuditWriter _auditWriter;
    private readonly IIntegrationEventCollector _eventCollector;
    private readonly TimeProvider _timeProvider;

    public UpdateReservationCommandHandler(
        IReservationReader reader,
        IPropertyReservationEligibilityReader eligibilityReader,
        IUpdateReservationExecutor executor,
        IReservationConflictGuard conflictGuard,
        IRepository<Reservation, Guid> repository,
        IReservationAuditWriter auditWriter,
        IIntegrationEventCollector eventCollector,
        TimeProvider timeProvider)
    {
        _reader = reader;
        _eligibilityReader = eligibilityReader;
        _executor = executor;
        _conflictGuard = conflictGuard;
        _repository = repository;
        _auditWriter = auditWriter;
        _eventCollector = eventCollector;
        _timeProvider = timeProvider;
    }

    public async ValueTask<Result<ReservationResult>> Handle(UpdateReservationCommand command, CancellationToken cancellationToken)
    {
        if (!command.PropertyId.IsSet && !command.GuestName.IsSet && !command.GuestPhone.IsSet &&
            !command.CheckInAt.IsSet && !command.CheckOutAt.IsSet && !command.GuestCount.IsSet)
        {
            return Result.Failure<ReservationResult>(NoChangesProvidedError);
        }

        // Steps 1-3.
        var snapshot = await _reader.GetUpdateSnapshotAsync(command.ReservationId, cancellationToken);
        if (snapshot is null)
            return Result.Failure<ReservationResult>(ReservationNotFoundError);

        // Checked before any diffing/no-op evaluation — even a PATCH whose
        // supplied values already match the current ones must be rejected
        // when the reservation is Cancelled.
        if (snapshot.Status == ReservationStatus.Cancelled)
            return Result.Failure<ReservationResult>(CancelledReservationCannotBeModifiedError);

        // Step 4.
        var finalPropertyId = command.PropertyId.IsSet ? command.PropertyId.Value : snapshot.PropertyId;
        var finalCheckInAt = command.CheckInAt.IsSet ? command.CheckInAt.Value : snapshot.CheckInAt;
        var finalCheckOutAt = command.CheckOutAt.IsSet ? command.CheckOutAt.Value : snapshot.CheckOutAt;
        var finalGuestCount = command.GuestCount.IsSet ? command.GuestCount.Value : snapshot.GuestCount;

        if (finalCheckOutAt <= finalCheckInAt)
            return Result.Failure<ReservationResult>(ScheduleInvalidError);

        // Steps 5-6.
        var eligibility = await _eligibilityReader.GetAsync(command.TenantId, finalPropertyId, cancellationToken);

        if (eligibility is null)
            return Result.Failure<ReservationResult>(PropertyNotFoundError);

        if (!eligibility.IsActive)
            return Result.Failure<ReservationResult>(PropertyNotActiveError);

        if (finalGuestCount > eligibility.Capacity)
            return Result.Failure<ReservationResult>(PropertyCapacityExceededError);

        // Step 7.
        return await _executor.ExecuteAsync(async () =>
        {
            // Step 8.
            var reservation = await _repository.GetByIdAsync(command.ReservationId, cancellationToken);
            if (reservation is null)
                return Result.Failure<ReservationResult>(ReservationNotFoundError);

            // Steps 9-10 — no automatic retry: the caller must resubmit with
            // a fresh read if it wants to try again.
            var currentXmin = await _reader.GetCurrentXminAsync(command.ReservationId, cancellationToken);
            if (currentXmin is null || currentXmin != snapshot.Xmin)
                return Result.Failure<ReservationResult>(ReservationConcurrencyConflictError);

            // Steps 11-12.
            await _conflictGuard.AcquirePropertyLockAsync(command.TenantId, finalPropertyId, cancellationToken);

            var hasConflict = await _conflictGuard.HasConflictingReservationAsync(
                command.TenantId, finalPropertyId, finalCheckInAt, finalCheckOutAt,
                excludeReservationId: reservation.Id, cancellationToken);

            if (hasConflict)
                return Result.Failure<ReservationResult>(ReservationDateConflictError);

            // Step 13.
            var now = _timeProvider.GetUtcNow();
            var changedFields = new List<string>();

            if (command.PropertyId.IsSet && finalPropertyId != reservation.PropertyId)
            {
                reservation.ChangeProperty(finalPropertyId, now);
                changedFields.Add("property_id");
            }

            if (command.GuestName.IsSet && !string.Equals(command.GuestName.Value!.Trim(), reservation.GuestName, StringComparison.Ordinal))
            {
                reservation.ChangeGuestName(command.GuestName.Value!, now);
                changedFields.Add("guest_name");
            }

            if (command.GuestPhone.IsSet)
            {
                var normalizedGuestPhone = string.IsNullOrWhiteSpace(command.GuestPhone.Value) ? null : command.GuestPhone.Value!.Trim();
                if (!string.Equals(normalizedGuestPhone, reservation.GuestPhone, StringComparison.Ordinal))
                {
                    reservation.ChangeGuestPhone(command.GuestPhone.Value, now);
                    changedFields.Add("guest_phone");
                }
            }

            var checkInChanged = command.CheckInAt.IsSet && finalCheckInAt != reservation.CheckInAt;
            var checkOutChanged = command.CheckOutAt.IsSet && finalCheckOutAt != reservation.CheckOutAt;
            if (checkInChanged || checkOutChanged)
            {
                reservation.Reschedule(finalCheckInAt, finalCheckOutAt, now);
                if (checkInChanged)
                    changedFields.Add("check_in_at");
                if (checkOutChanged)
                    changedFields.Add("check_out_at");
            }

            if (command.GuestCount.IsSet && finalGuestCount != reservation.GuestCount)
            {
                reservation.ChangeGuestCount(finalGuestCount, now);
                changedFields.Add("guest_count");
            }

            if (changedFields.Count > 0)
            {
                var correlationId = Guid.NewGuid();

                _auditWriter.Record(ReservationAuditEntry.Create(
                    Guid.NewGuid(), command.TenantId, command.ActorId, "Reservation", command.ReservationId,
                    "reservation_updated", changedFields, now));

                _eventCollector.Enqueue(new ReservationUpdated
                {
                    TenantId = command.TenantId,
                    AggregateId = command.ReservationId,
                    AggregateType = "Reservation",
                    CorrelationId = correlationId,
                    ActorType = "User",
                    ActorId = command.ActorId.ToString(),
                    ReservationId = command.ReservationId,
                    ChangedFields = changedFields,
                    CheckInAt = reservation.CheckInAt,
                    CheckOutAt = reservation.CheckOutAt,
                });
            }

            return Result.Success(CreateReservationCommandHandler.ToResult(reservation));
        }, cancellationToken);
        // Step 14 — commit — is IUpdateReservationExecutor/ReservationsOutboxTransactionExecutor's own responsibility.
    }
}
