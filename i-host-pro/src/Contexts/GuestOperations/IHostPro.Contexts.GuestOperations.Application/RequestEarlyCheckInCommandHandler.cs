using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.Configuration.Contracts;
using IHostPro.Contexts.GuestOperations.Contracts;
using IHostPro.Contexts.GuestOperations.Domain;
using IHostPro.Contexts.GuestOperations.Domain.Enums;
using IHostPro.Contexts.Housekeeping.Contracts;
using IHostPro.Contexts.Reservations.Contracts;
using Microsoft.Extensions.Logging;

namespace IHostPro.Contexts.GuestOperations.Application;

/// <summary>
/// Evaluates and decides an early check-in request in one synchronous step
/// (Fase 10, Checkpoint 3 mandate — no manual approval step exists).
///
/// Evaluation order, exactly as mandated: (1) the GuestStayOperation must
/// exist and be <c>Active</c>; (2) the Reservation must exist
/// (<see cref="IReservationScheduleReader"/>, synchronous exception #7) and
/// be <c>Confirmed</c>; (3) the requested time must be structurally earlier
/// than the Reservation's current <c>CheckInAt</c>; (4) no other
/// <c>Pending</c> request may already exist for this Reservation
/// (cardinality rule) — all four are precondition/validation failures
/// returned as a <see cref="Result{TValue}"/> failure BEFORE any
/// <see cref="EarlyCheckInRequest"/> row is created, never a
/// <see cref="EarlyCheckInRequestStatus.Denied"/> outcome. Only once a row
/// exists does policy evaluation begin: <c>NotConfigured</c> →
/// <see cref="EarlyCheckInDenialReason.PolicyNotConfigured"/>; not Allowed →
/// <see cref="EarlyCheckInDenialReason.PolicyNotAllowed"/>; before the
/// policy's EarliestTime → <see cref="EarlyCheckInDenialReason.BeforeEarliestTime"/>;
/// a schedule conflict → <see cref="EarlyCheckInDenialReason.ScheduleConflict"/>;
/// cleaning required but not completed
/// (<see cref="ICleaningReadinessReader"/>, synchronous exception #8) →
/// <see cref="EarlyCheckInDenialReason.CleaningNotReady"/>; otherwise
/// <see cref="EarlyCheckInRequestStatus.Approved"/>.
///
/// <see cref="PolicyEngineUnavailableException"/> is never caught here — it
/// propagates as an infrastructure failure, exactly like every other
/// consumer of a typed policy reader.
/// </summary>
public sealed class RequestEarlyCheckInCommandHandler : ICommandHandler<RequestEarlyCheckInCommand, EarlyCheckInRequestResult>
{
    private static readonly Error GuestStayOperationNotFoundError = new(
        GuestOperationsErrorCodes.GuestStayOperationNotFound, GuestOperationsErrorCodes.GuestStayOperationNotFound);
    private static readonly Error GuestStayOperationNotEligibleError = new(
        GuestOperationsErrorCodes.GuestStayOperationNotEligibleForEarlyCheckIn, GuestOperationsErrorCodes.GuestStayOperationNotEligibleForEarlyCheckIn);
    private static readonly Error ReservationNotFoundError = new(
        GuestOperationsErrorCodes.ReservationNotFound, GuestOperationsErrorCodes.ReservationNotFound);
    private static readonly Error ReservationNotConfirmedError = new(
        GuestOperationsErrorCodes.ReservationNotConfirmed, GuestOperationsErrorCodes.ReservationNotConfirmed);
    private static readonly Error InvalidTimeError = new(
        GuestOperationsErrorCodes.EarlyCheckInRequestInvalidTime, GuestOperationsErrorCodes.EarlyCheckInRequestInvalidTime);
    private static readonly Error AlreadyActiveError = new(
        GuestOperationsErrorCodes.EarlyCheckInRequestAlreadyActive, GuestOperationsErrorCodes.EarlyCheckInRequestAlreadyActive);

    private readonly IGuestStayOperationReader _stayReader;
    private readonly IRepository<GuestStayOperation, Guid> _stayRepository;
    private readonly IEarlyCheckInRequestReader _requestReader;
    private readonly IRepository<EarlyCheckInRequest, Guid> _requestRepository;
    private readonly IReservationScheduleReader _scheduleReader;
    private readonly ICleaningReadinessReader _cleaningReader;
    private readonly IEarlyCheckInPolicyReader _policyReader;
    private readonly IIntegrationEventCollector _eventCollector;
    private readonly IGuestOperationsTransactionExecutor _transactionExecutor;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<RequestEarlyCheckInCommandHandler> _logger;

    public RequestEarlyCheckInCommandHandler(
        IGuestStayOperationReader stayReader,
        IRepository<GuestStayOperation, Guid> stayRepository,
        IEarlyCheckInRequestReader requestReader,
        IRepository<EarlyCheckInRequest, Guid> requestRepository,
        IReservationScheduleReader scheduleReader,
        ICleaningReadinessReader cleaningReader,
        IEarlyCheckInPolicyReader policyReader,
        IIntegrationEventCollector eventCollector,
        IGuestOperationsTransactionExecutor transactionExecutor,
        TimeProvider timeProvider,
        ILogger<RequestEarlyCheckInCommandHandler> logger)
    {
        _stayReader = stayReader;
        _stayRepository = stayRepository;
        _requestReader = requestReader;
        _requestRepository = requestRepository;
        _scheduleReader = scheduleReader;
        _cleaningReader = cleaningReader;
        _policyReader = policyReader;
        _eventCollector = eventCollector;
        _transactionExecutor = transactionExecutor;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async ValueTask<Result<EarlyCheckInRequestResult>> Handle(RequestEarlyCheckInCommand command, CancellationToken cancellationToken) =>
        await _transactionExecutor.ExecuteAsync(async () =>
        {
            var operationId = await _stayReader.GetIdByReservationIdAsync(command.ReservationId, cancellationToken);
            var operation = operationId is null ? null : await _stayRepository.GetByIdAsync(operationId.Value, cancellationToken);

            if (operation is null)
            {
                _logger.LogWarning(
                    "RequestEarlyCheckIn failed for tenant {TenantId} reservationId {ReservationId}: {Result}",
                    command.TenantId, command.ReservationId, "GuestStayOperationNotFound");
                return Result.Failure<EarlyCheckInRequestResult>(GuestStayOperationNotFoundError);
            }

            if (operation.Status != GuestStayOperationStatus.Active)
            {
                _logger.LogWarning(
                    "RequestEarlyCheckIn rejected for tenant {TenantId} reservationId {ReservationId}: {Result}",
                    command.TenantId, command.ReservationId, "GuestStayOperationNotEligible");
                return Result.Failure<EarlyCheckInRequestResult>(GuestStayOperationNotEligibleError);
            }

            var schedule = await _scheduleReader.GetScheduleAsync(command.TenantId, command.ReservationId, cancellationToken);

            if (schedule is null)
            {
                _logger.LogWarning(
                    "RequestEarlyCheckIn failed for tenant {TenantId} reservationId {ReservationId}: {Result}",
                    command.TenantId, command.ReservationId, "ReservationNotFound");
                return Result.Failure<EarlyCheckInRequestResult>(ReservationNotFoundError);
            }

            if (schedule.Status != "confirmed")
            {
                _logger.LogWarning(
                    "RequestEarlyCheckIn rejected for tenant {TenantId} reservationId {ReservationId}: {Result}",
                    command.TenantId, command.ReservationId, "ReservationNotConfirmed");
                return Result.Failure<EarlyCheckInRequestResult>(ReservationNotConfirmedError);
            }

            if (command.RequestedCheckInAt >= schedule.CheckInAt)
            {
                _logger.LogWarning(
                    "RequestEarlyCheckIn rejected for tenant {TenantId} reservationId {ReservationId}: {Result}",
                    command.TenantId, command.ReservationId, "InvalidTime");
                return Result.Failure<EarlyCheckInRequestResult>(InvalidTimeError);
            }

            if (await _requestReader.HasActiveRequestAsync(command.ReservationId, cancellationToken))
            {
                _logger.LogWarning(
                    "RequestEarlyCheckIn rejected for tenant {TenantId} reservationId {ReservationId}: {Result}",
                    command.TenantId, command.ReservationId, "AlreadyActive");
                return Result.Failure<EarlyCheckInRequestResult>(AlreadyActiveError);
            }

            var policyResult = await _policyReader.GetEffectiveAsync(command.TenantId, operation.PropertyId, cancellationToken);

            var now = _timeProvider.GetUtcNow();
            var request = EarlyCheckInRequest.Create(
                Guid.NewGuid(), command.TenantId, command.ReservationId, operation.PropertyId, command.RequestedCheckInAt, now);
            _requestRepository.Add(request);

            if (policyResult.Status == PolicyReadStatus.NotConfigured)
            {
                return Result.Success(Deny(request, EarlyCheckInDenialReason.PolicyNotConfigured, now, command.TenantId));
            }

            var policy = policyResult.Value!;

            if (!policy.Allowed)
            {
                return Result.Success(Deny(request, EarlyCheckInDenialReason.PolicyNotAllowed, now, command.TenantId));
            }

            if (policy.EarliestTime is { } earliestTime && command.RequestedCheckInAt.TimeOfDay < earliestTime.ToTimeSpan())
            {
                return Result.Success(Deny(request, EarlyCheckInDenialReason.BeforeEarliestTime, now, command.TenantId));
            }

            var hasConflict = await _scheduleReader.HasConflictingReservationAsync(
                command.TenantId, command.ReservationId, command.RequestedCheckInAt, schedule.CheckOutAt, cancellationToken);

            if (hasConflict)
            {
                return Result.Success(Deny(request, EarlyCheckInDenialReason.ScheduleConflict, now, command.TenantId));
            }

            if (policy.RequiresCleaningCompleted)
            {
                var cleaningReady = await _cleaningReader.IsCleaningCompletedAsync(command.TenantId, command.ReservationId, cancellationToken);

                if (!cleaningReady)
                {
                    return Result.Success(Deny(request, EarlyCheckInDenialReason.CleaningNotReady, now, command.TenantId));
                }
            }

            request.Approve(now);

            _eventCollector.Enqueue(new EarlyCheckinApproved
            {
                TenantId = command.TenantId,
                AggregateId = request.Id,
                AggregateType = "EarlyCheckInRequest",
                CorrelationId = Guid.NewGuid(),
                ActorType = "System",
                ReservationId = command.ReservationId,
                ApprovedCheckInAt = command.RequestedCheckInAt,
            });

            _logger.LogInformation(
                "Early check-in approved for tenant {TenantId} reservationId {ReservationId}",
                command.TenantId, command.ReservationId);

            return Result.Success(ToResult(request));
        }, cancellationToken);

    private EarlyCheckInRequestResult Deny(EarlyCheckInRequest request, EarlyCheckInDenialReason reason, DateTimeOffset now, Guid tenantId)
    {
        request.Deny(reason, now);

        _eventCollector.Enqueue(new EarlyCheckinDenied
        {
            TenantId = tenantId,
            AggregateId = request.Id,
            AggregateType = "EarlyCheckInRequest",
            CorrelationId = Guid.NewGuid(),
            ActorType = "System",
            ReservationId = request.ReservationId,
            ReasonCode = EarlyCheckInRequestStatusCodeMapper.ToCode(reason),
        });

        _logger.LogInformation(
            "Early check-in denied for tenant {TenantId} reservationId {ReservationId}: {Reason}",
            tenantId, request.ReservationId, reason);

        return ToResult(request);
    }

    private static EarlyCheckInRequestResult ToResult(EarlyCheckInRequest request) => new(
        request.Id,
        request.ReservationId,
        request.RequestedCheckInAt,
        EarlyCheckInRequestStatusCodeMapper.ToCode(request.Status),
        request.DenialReason is { } reason ? EarlyCheckInRequestStatusCodeMapper.ToCode(reason) : null,
        request.CreatedAtUtc,
        request.DecidedAtUtc,
        request.UpdatedAtUtc);
}
