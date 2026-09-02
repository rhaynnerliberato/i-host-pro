using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.GuestOperations.Contracts;
using IHostPro.Contexts.GuestOperations.Domain;
using IHostPro.Contexts.GuestOperations.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace IHostPro.Contexts.GuestOperations.Application;

/// <summary>
/// Records a guest's real check-in (Fase 10, Checkpoint 2 — Check-in/Checkout
/// Core): resolves the <see cref="GuestStayOperation"/> for the given
/// Reservation, transitions Active -&gt; CheckedIn, and publishes
/// <see cref="GuestCheckedIn"/> exactly once. An already-CheckedIn operation
/// is a silent idempotent no-op (never republishes). An already-CheckedOut
/// operation is a terminal-state violation — <see cref="Result{TValue}"/>
/// failure with <see cref="GuestOperationsErrorCodes.GuestStayOperationAlreadyCheckedOut"/>,
/// never restored. A missing operation is
/// <see cref="GuestOperationsErrorCodes.GuestStayOperationNotFound"/>.
/// </summary>
public sealed class RecordGuestCheckedInCommandHandler : ICommandHandler<RecordGuestCheckedInCommand, GuestStayOperationResult>
{
    private static readonly Error NotFoundError = new(
        GuestOperationsErrorCodes.GuestStayOperationNotFound, GuestOperationsErrorCodes.GuestStayOperationNotFound);
    private static readonly Error AlreadyCheckedOutError = new(
        GuestOperationsErrorCodes.GuestStayOperationAlreadyCheckedOut, GuestOperationsErrorCodes.GuestStayOperationAlreadyCheckedOut);

    private readonly IGuestStayOperationReader _reader;
    private readonly IRepository<GuestStayOperation, Guid> _repository;
    private readonly IIntegrationEventCollector _eventCollector;
    private readonly IGuestOperationsTransactionExecutor _transactionExecutor;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<RecordGuestCheckedInCommandHandler> _logger;

    public RecordGuestCheckedInCommandHandler(
        IGuestStayOperationReader reader,
        IRepository<GuestStayOperation, Guid> repository,
        IIntegrationEventCollector eventCollector,
        IGuestOperationsTransactionExecutor transactionExecutor,
        TimeProvider timeProvider,
        ILogger<RecordGuestCheckedInCommandHandler> logger)
    {
        _reader = reader;
        _repository = repository;
        _eventCollector = eventCollector;
        _transactionExecutor = transactionExecutor;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async ValueTask<Result<GuestStayOperationResult>> Handle(RecordGuestCheckedInCommand command, CancellationToken cancellationToken) =>
        await _transactionExecutor.ExecuteAsync(async () =>
        {
            var operationId = await _reader.GetIdByReservationIdAsync(command.ReservationId, cancellationToken);

            if (operationId is null)
            {
                _logger.LogWarning(
                    "RecordGuestCheckedIn failed for tenant {TenantId} reservationId {ReservationId}: {Result}",
                    command.TenantId, command.ReservationId, "GuestStayOperationNotFound");
                return Result.Failure<GuestStayOperationResult>(NotFoundError);
            }

            var operation = await _repository.GetByIdAsync(operationId.Value, cancellationToken);

            if (operation is null)
            {
                _logger.LogWarning(
                    "RecordGuestCheckedIn failed for tenant {TenantId} reservationId {ReservationId}: {Result}",
                    command.TenantId, command.ReservationId, "GuestStayOperationNotFound");
                return Result.Failure<GuestStayOperationResult>(NotFoundError);
            }

            if (operation.Status == GuestStayOperationStatus.CheckedIn)
            {
                _logger.LogInformation(
                    "RecordGuestCheckedIn no-op for tenant {TenantId} reservationId {ReservationId}: {Result}",
                    command.TenantId, command.ReservationId, "AlreadyCheckedIn");
                return Result.Success(ToResult(operation));
            }

            if (operation.Status == GuestStayOperationStatus.CheckedOut)
            {
                _logger.LogWarning(
                    "RecordGuestCheckedIn rejected for tenant {TenantId} reservationId {ReservationId}: {Result}",
                    command.TenantId, command.ReservationId, "AlreadyCheckedOut");
                return Result.Failure<GuestStayOperationResult>(AlreadyCheckedOutError);
            }

            var now = _timeProvider.GetUtcNow();
            operation.CheckIn(now);
            _repository.Update(operation);

            _eventCollector.Enqueue(new GuestCheckedIn
            {
                TenantId = command.TenantId,
                AggregateId = operation.Id,
                AggregateType = "GuestStayOperation",
                CorrelationId = Guid.NewGuid(),
                ActorType = "User",
                ActorId = command.ActorId.ToString(),
                ReservationId = command.ReservationId,
                PropertyId = operation.PropertyId,
                CheckedInAtUtc = now,
            });

            _logger.LogInformation(
                "Guest checked in for tenant {TenantId} reservationId {ReservationId}",
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
