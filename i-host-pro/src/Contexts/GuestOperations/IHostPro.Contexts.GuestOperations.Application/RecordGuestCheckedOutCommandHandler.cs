using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.GuestOperations.Contracts;
using IHostPro.Contexts.GuestOperations.Domain;
using IHostPro.Contexts.GuestOperations.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace IHostPro.Contexts.GuestOperations.Application;

/// <summary>
/// Records a guest's real checkout (Fase 10, Checkpoint 1 — Guest Operations
/// Foundation): resolves the <see cref="GuestStayOperation"/> owning the
/// given Reservation, marks it checked out, and publishes
/// <see cref="GuestCheckedOut"/> exactly once. An already-CheckedOut
/// operation is a silent idempotent no-op (never re-throws
/// <see cref="GuestStayOperation.CheckOut"/>'s own guard, never
/// republishes) — mirrors <c>Reservations.Application.CloseReservationCommandHandler</c>'s
/// own idempotency shape. A missing operation (no <see cref="GuestStayOperation"/>
/// exists for this Reservation) is a generic anomaly and throws a plain
/// <see cref="InvalidOperationException"/>, relying on Wolverine's default
/// redelivery — no custom retry policy.
/// </summary>
public sealed class RecordGuestCheckedOutCommandHandler : IRecordGuestCheckedOutHandler
{
    private readonly IGuestStayOperationReader _reader;
    private readonly IRepository<GuestStayOperation, Guid> _repository;
    private readonly IIntegrationEventCollector _eventCollector;
    private readonly IGuestOperationsTransactionExecutor _transactionExecutor;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<RecordGuestCheckedOutCommandHandler> _logger;

    public RecordGuestCheckedOutCommandHandler(
        IGuestStayOperationReader reader,
        IRepository<GuestStayOperation, Guid> repository,
        IIntegrationEventCollector eventCollector,
        IGuestOperationsTransactionExecutor transactionExecutor,
        TimeProvider timeProvider,
        ILogger<RecordGuestCheckedOutCommandHandler> logger)
    {
        _reader = reader;
        _repository = repository;
        _eventCollector = eventCollector;
        _transactionExecutor = transactionExecutor;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public Task HandleAsync(RecordGuestCheckedOutCommand command, CancellationToken cancellationToken) =>
        _transactionExecutor.ExecuteAsync(async () =>
        {
            var operationId = await _reader.GetIdByReservationIdAsync(command.ReservationId, cancellationToken);

            if (operationId is null)
            {
                throw new InvalidOperationException(
                    $"RecordGuestCheckedOut: no GuestStayOperation found for reservation '{command.ReservationId}' " +
                    $"for tenant '{command.TenantId}' — relies on Wolverine's own default redelivery behavior; " +
                    "no custom retry policy introduced.");
            }

            var operation = await _repository.GetByIdAsync(operationId.Value, cancellationToken);

            if (operation is null)
            {
                throw new InvalidOperationException(
                    $"RecordGuestCheckedOut: GuestStayOperation '{operationId.Value}' no longer exists for tenant " +
                    $"'{command.TenantId}'.");
            }

            if (operation.Status == GuestStayOperationStatus.CheckedOut)
            {
                _logger.LogInformation(
                    "RecordGuestCheckedOut no-op for tenant {TenantId} reservationId {ReservationId}: {Result}",
                    command.TenantId, command.ReservationId, "AlreadyCheckedOut");
                return true;
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
                ActorType = "System",
                ReservationId = command.ReservationId,
            });

            _logger.LogInformation(
                "Guest checked out for tenant {TenantId} reservationId {ReservationId}",
                command.TenantId, command.ReservationId);

            return true;
        }, cancellationToken);
}
