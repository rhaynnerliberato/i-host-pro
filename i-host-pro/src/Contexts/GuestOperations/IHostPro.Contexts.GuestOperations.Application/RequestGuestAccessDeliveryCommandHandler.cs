using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.GuestOperations.Contracts;
using IHostPro.Contexts.GuestOperations.Domain;
using IHostPro.Contexts.GuestOperations.Domain.Enums;
using IHostPro.Contexts.Reservations.Contracts;
using Microsoft.Extensions.Logging;

namespace IHostPro.Contexts.GuestOperations.Application;

/// <summary>
/// Validates preconditions and publishes <see cref="GuestAccessDeliveryRequested"/>
/// (Fase 10, Checkpoint 6.2 — Guest Access Secure Delivery Corrective
/// Implementation). Mutates NO domain state of its own — no field is added
/// to <c>GuestStayOperation</c> just because this command ran (CP6.1
/// Decision Gate item 26/27: <c>Message</c> lifecycle in Communication is
/// the audit source, not a new persisted flag here). Idempotency against a
/// repeated request is Communication's own responsibility (per-intent
/// idempotency key, mirrors every other delivery processor) — this handler
/// simply re-validates and re-publishes every time it is called.
///
/// Preconditions (CP6.2 mandate item 11): the Reservation must be
/// <c>Confirmed</c> (via <see cref="IReservationScheduleReader"/>, the
/// already-approved exception #7 — no new synchronous exception needed);
/// the <c>GuestStayOperation</c> must not yet be <see cref="GuestStayOperationStatus.CheckedOut"/>.
/// <see cref="GuestStayOperationStatus.CheckedIn"/> is explicitly ALLOWED
/// (re-sending access after check-in is a legitimate operational need — e.g.
/// the guest lost their code), matching the mandate's own explicit
/// preference not to block a resend just because check-in already happened.
/// </summary>
public sealed class RequestGuestAccessDeliveryCommandHandler : ICommandHandler<RequestGuestAccessDeliveryCommand, GuestStayOperationResult>
{
    private static readonly Error NotFoundError = new(
        GuestOperationsErrorCodes.GuestStayOperationNotFound, GuestOperationsErrorCodes.GuestStayOperationNotFound);
    private static readonly Error AlreadyCheckedOutError = new(
        GuestOperationsErrorCodes.GuestStayOperationAlreadyCheckedOut, GuestOperationsErrorCodes.GuestStayOperationAlreadyCheckedOut);
    private static readonly Error ReservationNotFoundError = new(
        GuestOperationsErrorCodes.ReservationNotFound, GuestOperationsErrorCodes.ReservationNotFound);
    private static readonly Error ReservationNotConfirmedError = new(
        GuestOperationsErrorCodes.ReservationNotConfirmed, GuestOperationsErrorCodes.ReservationNotConfirmed);

    private readonly IGuestStayOperationReader _reader;
    private readonly IRepository<GuestStayOperation, Guid> _repository;
    private readonly IReservationScheduleReader _scheduleReader;
    private readonly IIntegrationEventCollector _eventCollector;
    private readonly IGuestOperationsTransactionExecutor _transactionExecutor;
    private readonly ILogger<RequestGuestAccessDeliveryCommandHandler> _logger;

    public RequestGuestAccessDeliveryCommandHandler(
        IGuestStayOperationReader reader,
        IRepository<GuestStayOperation, Guid> repository,
        IReservationScheduleReader scheduleReader,
        IIntegrationEventCollector eventCollector,
        IGuestOperationsTransactionExecutor transactionExecutor,
        ILogger<RequestGuestAccessDeliveryCommandHandler> logger)
    {
        _reader = reader;
        _repository = repository;
        _scheduleReader = scheduleReader;
        _eventCollector = eventCollector;
        _transactionExecutor = transactionExecutor;
        _logger = logger;
    }

    public async ValueTask<Result<GuestStayOperationResult>> Handle(
        RequestGuestAccessDeliveryCommand command, CancellationToken cancellationToken) =>
        await _transactionExecutor.ExecuteAsync(async () =>
        {
            var operationId = await _reader.GetIdByReservationIdAsync(command.ReservationId, cancellationToken);
            var operation = operationId is null ? null : await _repository.GetByIdAsync(operationId.Value, cancellationToken);

            if (operation is null)
            {
                _logger.LogWarning(
                    "RequestGuestAccessDelivery failed for tenant {TenantId} reservationId {ReservationId}: {Result}",
                    command.TenantId, command.ReservationId, "GuestStayOperationNotFound");
                return Result.Failure<GuestStayOperationResult>(NotFoundError);
            }

            if (operation.Status == GuestStayOperationStatus.CheckedOut)
            {
                _logger.LogWarning(
                    "RequestGuestAccessDelivery rejected for tenant {TenantId} reservationId {ReservationId}: {Result}",
                    command.TenantId, command.ReservationId, "AlreadyCheckedOut");
                return Result.Failure<GuestStayOperationResult>(AlreadyCheckedOutError);
            }

            var schedule = await _scheduleReader.GetScheduleAsync(command.TenantId, command.ReservationId, cancellationToken);
            if (schedule is null)
            {
                _logger.LogWarning(
                    "RequestGuestAccessDelivery failed for tenant {TenantId} reservationId {ReservationId}: {Result}",
                    command.TenantId, command.ReservationId, "ReservationNotFound");
                return Result.Failure<GuestStayOperationResult>(ReservationNotFoundError);
            }

            if (schedule.Status != "confirmed")
            {
                _logger.LogWarning(
                    "RequestGuestAccessDelivery rejected for tenant {TenantId} reservationId {ReservationId}: {Result}",
                    command.TenantId, command.ReservationId, "ReservationNotConfirmed");
                return Result.Failure<GuestStayOperationResult>(ReservationNotConfirmedError);
            }

            _eventCollector.Enqueue(new GuestAccessDeliveryRequested
            {
                TenantId = command.TenantId,
                AggregateId = operation.Id,
                AggregateType = "GuestStayOperation",
                CorrelationId = Guid.NewGuid(),
                ActorType = "System",
                ReservationId = command.ReservationId,
                PropertyId = operation.PropertyId,
            });

            _logger.LogInformation(
                "Guest access delivery requested for tenant {TenantId} reservationId {ReservationId}",
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
