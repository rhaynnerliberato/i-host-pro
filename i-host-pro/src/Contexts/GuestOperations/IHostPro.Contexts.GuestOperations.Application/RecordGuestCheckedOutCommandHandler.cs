using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.GuestOperations.Contracts;
using IHostPro.Contexts.GuestOperations.Domain;
using IHostPro.Contexts.GuestOperations.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace IHostPro.Contexts.GuestOperations.Application;

/// <summary>
/// Records a guest's real checkout (Fase 10, Checkpoint 1 — Guest Operations
/// Foundation; Checkpoint 2 — Check-in/Checkout Core): resolves the
/// <see cref="GuestStayOperation"/> owning the given Reservation,
/// transitions CheckedIn -&gt; CheckedOut, and publishes
/// <see cref="GuestCheckedOut"/> exactly once. An already-CheckedOut
/// operation is a silent idempotent no-op (never re-throws
/// <see cref="GuestStayOperation.CheckOut"/>'s own guard, never
/// republishes). A checkout attempted while still <see cref="GuestStayOperationStatus.Active"/>
/// (never checked in) is an operational-inconsistency violation — a
/// <see cref="Result{TValue}"/> failure with
/// <see cref="GuestOperationsErrorCodes.GuestStayOperationNotCheckedIn"/>
/// (Fase 10, Checkpoint 2 decision), never a silent skip.
///
/// Returns <see cref="Result{TValue}"/>, not a thrown exception, for every
/// expected outcome (Checkpoint 2): this command is now dispatched via
/// Mediator from <c>GuestOperations.Api</c>'s HTTP controller, and every
/// other HTTP-facing command handler in this codebase uses the Result/Error
/// convention — never a raw exception reaching the Api layer.
/// </summary>
public sealed class RecordGuestCheckedOutCommandHandler : ICommandHandler<RecordGuestCheckedOutCommand, GuestStayOperationResult>
{
    private static readonly Error NotFoundError = new(
        GuestOperationsErrorCodes.GuestStayOperationNotFound, GuestOperationsErrorCodes.GuestStayOperationNotFound);
    private static readonly Error NotCheckedInError = new(
        GuestOperationsErrorCodes.GuestStayOperationNotCheckedIn, GuestOperationsErrorCodes.GuestStayOperationNotCheckedIn);

    private readonly IGuestStayOperationReader _reader;
    private readonly IRepository<GuestStayOperation, Guid> _repository;
    private readonly IIntegrationEventCollector _eventCollector;
    private readonly IGuestStayOperationAuditWriter _auditWriter;
    private readonly IGuestOperationsTransactionExecutor _transactionExecutor;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<RecordGuestCheckedOutCommandHandler> _logger;

    public RecordGuestCheckedOutCommandHandler(
        IGuestStayOperationReader reader,
        IRepository<GuestStayOperation, Guid> repository,
        IIntegrationEventCollector eventCollector,
        IGuestStayOperationAuditWriter auditWriter,
        IGuestOperationsTransactionExecutor transactionExecutor,
        TimeProvider timeProvider,
        ILogger<RecordGuestCheckedOutCommandHandler> logger)
    {
        _reader = reader;
        _repository = repository;
        _eventCollector = eventCollector;
        _auditWriter = auditWriter;
        _transactionExecutor = transactionExecutor;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async ValueTask<Result<GuestStayOperationResult>> Handle(RecordGuestCheckedOutCommand command, CancellationToken cancellationToken) =>
        await _transactionExecutor.ExecuteAsync(async () =>
        {
            var operationId = await _reader.GetIdByReservationIdAsync(command.ReservationId, cancellationToken);

            if (operationId is null)
            {
                _logger.LogWarning(
                    "RecordGuestCheckedOut failed for tenant {TenantId} reservationId {ReservationId}: {Result}",
                    command.TenantId, command.ReservationId, "GuestStayOperationNotFound");
                return Result.Failure<GuestStayOperationResult>(NotFoundError);
            }

            var operation = await _repository.GetByIdAsync(operationId.Value, cancellationToken);

            if (operation is null)
            {
                _logger.LogWarning(
                    "RecordGuestCheckedOut failed for tenant {TenantId} reservationId {ReservationId}: {Result}",
                    command.TenantId, command.ReservationId, "GuestStayOperationNotFound");
                return Result.Failure<GuestStayOperationResult>(NotFoundError);
            }

            if (operation.Status == GuestStayOperationStatus.CheckedOut)
            {
                _logger.LogInformation(
                    "RecordGuestCheckedOut no-op for tenant {TenantId} reservationId {ReservationId}: {Result}",
                    command.TenantId, command.ReservationId, "AlreadyCheckedOut");
                return Result.Success(ToResult(operation));
            }

            if (operation.Status == GuestStayOperationStatus.Active)
            {
                _logger.LogWarning(
                    "RecordGuestCheckedOut rejected for tenant {TenantId} reservationId {ReservationId}: {Result}",
                    command.TenantId, command.ReservationId, "NotCheckedIn");
                return Result.Failure<GuestStayOperationResult>(NotCheckedInError);
            }

            var now = _timeProvider.GetUtcNow();
            operation.CheckOut(now);
            _repository.Update(operation);

            _eventCollector.Enqueue(new GuestCheckedOut
            {
                TenantId = command.TenantId,
                AggregateId = operation.Id,
                AggregateType = "GuestStayOperation",
                CorrelationId = Guid.NewGuid(),
                ActorType = "User",
                ActorId = command.ActorId.ToString(),
                ReservationId = command.ReservationId,
            });

            _auditWriter.Record(GuestStayOperationAuditEntry.Record(
                Guid.NewGuid(), command.TenantId, operation.Id, GuestStayOperationAuditAction.CheckedOut,
                "User", command.ActorId, now));

            _logger.LogInformation(
                "Guest checked out for tenant {TenantId} reservationId {ReservationId}",
                command.TenantId, command.ReservationId);

            return Result.Success(ToResult(operation));
        }, cancellationToken);

    private static GuestStayOperationResult ToResult(GuestStayOperation operation) => new(
        operation.Id,
        operation.ReservationId,
        operation.PropertyId,
        GuestStayOperationStatusCodeMapper.ToCode(operation.Status),
        operation.CheckedInAtUtc,
        operation.CheckedOutAtUtc,
        operation.CreatedAtUtc,
        operation.UpdatedAtUtc);
}
