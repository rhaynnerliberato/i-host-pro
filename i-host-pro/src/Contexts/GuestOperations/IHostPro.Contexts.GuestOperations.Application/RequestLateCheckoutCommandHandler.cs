using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.Configuration.Contracts;
using IHostPro.Contexts.GuestOperations.Contracts;
using IHostPro.Contexts.GuestOperations.Domain;
using IHostPro.Contexts.GuestOperations.Domain.Enums;
using IHostPro.Contexts.Reservations.Contracts;
using Microsoft.Extensions.Logging;
using ConfigurationChargeType = IHostPro.Contexts.Configuration.Contracts.LateCheckoutChargeType;
using DomainChargeType = IHostPro.Contexts.GuestOperations.Domain.Enums.LateCheckoutChargeType;

namespace IHostPro.Contexts.GuestOperations.Application;

/// <summary>
/// Evaluates and decides a late checkout request in one synchronous step
/// (Fase 10, Checkpoint 3 mandate), mirroring
/// <see cref="RequestEarlyCheckInCommandHandler"/>'s own structure with two
/// deliberate differences.
///
/// First, the effective <c>LateCheckoutPolicy</c> is read BEFORE a
/// <see cref="LateCheckoutRequest"/> row is ever created — solely to reject
/// <see cref="ConfigurationChargeType.Percentage"/> as an explicit functional
/// error (<see cref="GuestOperationsErrorCodes.LateCheckoutChargeTypePercentageUnsupported"/>),
/// officially unsupported pending a future pricing domain: no row can even
/// snapshot a charge type this aggregate refuses to hold
/// (<see cref="LateCheckoutRequest.Create"/> itself guards this). Every
/// other read of that SAME <c>policyResult</c> — Allowed, LatestTime,
/// UpdatesCleaning — is reused for the post-creation evaluation, never a
/// second read.
///
/// Second, when the request would otherwise be approved and the policy's
/// <c>RequiresPix</c> is true, the outcome is
/// <see cref="LateCheckoutRequestStatus.PendingPayment"/> instead of
/// <see cref="LateCheckoutRequestStatus.Approved"/> — Reservation's schedule
/// is never altered for that outcome (Fase 10, Checkpoint 5 closes the
/// payment loop). Housekeeping's own reaction (gated on
/// <c>UpdatesCleaning</c>) only ever fires alongside a true
/// <see cref="LateCheckoutApproved"/>, never a <c>PendingPayment</c> one.
/// </summary>
public sealed class RequestLateCheckoutCommandHandler : ICommandHandler<RequestLateCheckoutCommand, LateCheckoutRequestResult>
{
    private static readonly Error GuestStayOperationNotFoundError = new(
        GuestOperationsErrorCodes.GuestStayOperationNotFound, GuestOperationsErrorCodes.GuestStayOperationNotFound);
    private static readonly Error GuestStayOperationNotEligibleError = new(
        GuestOperationsErrorCodes.GuestStayOperationNotEligibleForLateCheckout, GuestOperationsErrorCodes.GuestStayOperationNotEligibleForLateCheckout);
    private static readonly Error ReservationNotFoundError = new(
        GuestOperationsErrorCodes.ReservationNotFound, GuestOperationsErrorCodes.ReservationNotFound);
    private static readonly Error ReservationNotConfirmedError = new(
        GuestOperationsErrorCodes.ReservationNotConfirmed, GuestOperationsErrorCodes.ReservationNotConfirmed);
    private static readonly Error InvalidTimeError = new(
        GuestOperationsErrorCodes.LateCheckoutRequestInvalidTime, GuestOperationsErrorCodes.LateCheckoutRequestInvalidTime);
    private static readonly Error AlreadyActiveError = new(
        GuestOperationsErrorCodes.LateCheckoutRequestAlreadyActive, GuestOperationsErrorCodes.LateCheckoutRequestAlreadyActive);
    private static readonly Error PercentageUnsupportedError = new(
        GuestOperationsErrorCodes.LateCheckoutChargeTypePercentageUnsupported, GuestOperationsErrorCodes.LateCheckoutChargeTypePercentageUnsupported);

    private readonly IGuestStayOperationReader _stayReader;
    private readonly IRepository<GuestStayOperation, Guid> _stayRepository;
    private readonly ILateCheckoutRequestReader _requestReader;
    private readonly IRepository<LateCheckoutRequest, Guid> _requestRepository;
    private readonly IReservationScheduleReader _scheduleReader;
    private readonly ILateCheckoutPolicyReader _policyReader;
    private readonly IIntegrationEventCollector _eventCollector;
    private readonly IGuestOperationsTransactionExecutor _transactionExecutor;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<RequestLateCheckoutCommandHandler> _logger;

    public RequestLateCheckoutCommandHandler(
        IGuestStayOperationReader stayReader,
        IRepository<GuestStayOperation, Guid> stayRepository,
        ILateCheckoutRequestReader requestReader,
        IRepository<LateCheckoutRequest, Guid> requestRepository,
        IReservationScheduleReader scheduleReader,
        ILateCheckoutPolicyReader policyReader,
        IIntegrationEventCollector eventCollector,
        IGuestOperationsTransactionExecutor transactionExecutor,
        TimeProvider timeProvider,
        ILogger<RequestLateCheckoutCommandHandler> logger)
    {
        _stayReader = stayReader;
        _stayRepository = stayRepository;
        _requestReader = requestReader;
        _requestRepository = requestRepository;
        _scheduleReader = scheduleReader;
        _policyReader = policyReader;
        _eventCollector = eventCollector;
        _transactionExecutor = transactionExecutor;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async ValueTask<Result<LateCheckoutRequestResult>> Handle(RequestLateCheckoutCommand command, CancellationToken cancellationToken) =>
        await _transactionExecutor.ExecuteAsync(async () =>
        {
            var operationId = await _stayReader.GetIdByReservationIdAsync(command.ReservationId, cancellationToken);
            var operation = operationId is null ? null : await _stayRepository.GetByIdAsync(operationId.Value, cancellationToken);

            if (operation is null)
            {
                _logger.LogWarning(
                    "RequestLateCheckout failed for tenant {TenantId} reservationId {ReservationId}: {Result}",
                    command.TenantId, command.ReservationId, "GuestStayOperationNotFound");
                return Result.Failure<LateCheckoutRequestResult>(GuestStayOperationNotFoundError);
            }

            if (operation.Status != GuestStayOperationStatus.CheckedIn)
            {
                _logger.LogWarning(
                    "RequestLateCheckout rejected for tenant {TenantId} reservationId {ReservationId}: {Result}",
                    command.TenantId, command.ReservationId, "GuestStayOperationNotEligible");
                return Result.Failure<LateCheckoutRequestResult>(GuestStayOperationNotEligibleError);
            }

            var schedule = await _scheduleReader.GetScheduleAsync(command.TenantId, command.ReservationId, cancellationToken);

            if (schedule is null)
            {
                _logger.LogWarning(
                    "RequestLateCheckout failed for tenant {TenantId} reservationId {ReservationId}: {Result}",
                    command.TenantId, command.ReservationId, "ReservationNotFound");
                return Result.Failure<LateCheckoutRequestResult>(ReservationNotFoundError);
            }

            if (schedule.Status != "confirmed")
            {
                _logger.LogWarning(
                    "RequestLateCheckout rejected for tenant {TenantId} reservationId {ReservationId}: {Result}",
                    command.TenantId, command.ReservationId, "ReservationNotConfirmed");
                return Result.Failure<LateCheckoutRequestResult>(ReservationNotConfirmedError);
            }

            if (command.RequestedCheckOutAt <= schedule.CheckOutAt)
            {
                _logger.LogWarning(
                    "RequestLateCheckout rejected for tenant {TenantId} reservationId {ReservationId}: {Result}",
                    command.TenantId, command.ReservationId, "InvalidTime");
                return Result.Failure<LateCheckoutRequestResult>(InvalidTimeError);
            }

            if (await _requestReader.HasActiveRequestAsync(command.ReservationId, cancellationToken))
            {
                _logger.LogWarning(
                    "RequestLateCheckout rejected for tenant {TenantId} reservationId {ReservationId}: {Result}",
                    command.TenantId, command.ReservationId, "AlreadyActive");
                return Result.Failure<LateCheckoutRequestResult>(AlreadyActiveError);
            }

            var policyResult = await _policyReader.GetEffectiveAsync(command.TenantId, operation.PropertyId, cancellationToken);

            if (policyResult.Status == PolicyReadStatus.Resolved && policyResult.Value!.ChargeType == ConfigurationChargeType.Percentage)
            {
                _logger.LogWarning(
                    "RequestLateCheckout rejected for tenant {TenantId} reservationId {ReservationId}: {Result}",
                    command.TenantId, command.ReservationId, "PercentageChargeTypeUnsupported");
                return Result.Failure<LateCheckoutRequestResult>(PercentageUnsupportedError);
            }

            var now = _timeProvider.GetUtcNow();

            var (chargeType, chargeValue, requiresPix) = policyResult.Status == PolicyReadStatus.Resolved
                ? (ToDomainChargeType(policyResult.Value!.ChargeType), policyResult.Value!.ChargeValue, policyResult.Value!.RequiresPix)
                : (DomainChargeType.None, (decimal?)null, false);

            var request = LateCheckoutRequest.Create(
                Guid.NewGuid(), command.TenantId, command.ReservationId, operation.PropertyId,
                command.RequestedCheckOutAt, chargeType, chargeValue, requiresPix, now);
            _requestRepository.Add(request);

            if (policyResult.Status == PolicyReadStatus.NotConfigured)
            {
                return Result.Success(Deny(request, LateCheckoutDenialReason.PolicyNotConfigured, now, command.TenantId));
            }

            var policy = policyResult.Value!;

            if (!policy.Allowed)
            {
                return Result.Success(Deny(request, LateCheckoutDenialReason.PolicyNotAllowed, now, command.TenantId));
            }

            if (policy.LatestTime is { } latestTime && command.RequestedCheckOutAt.TimeOfDay > latestTime.ToTimeSpan())
            {
                return Result.Success(Deny(request, LateCheckoutDenialReason.AfterLatestTime, now, command.TenantId));
            }

            var hasConflict = await _scheduleReader.HasConflictingReservationAsync(
                command.TenantId, command.ReservationId, schedule.CheckInAt, command.RequestedCheckOutAt, cancellationToken);

            if (hasConflict)
            {
                return Result.Success(Deny(request, LateCheckoutDenialReason.ScheduleConflict, now, command.TenantId));
            }

            if (policy.RequiresPix)
            {
                request.MarkPendingPayment(now);

                _logger.LogInformation(
                    "Late checkout pending payment for tenant {TenantId} reservationId {ReservationId}",
                    command.TenantId, command.ReservationId);

                return Result.Success(ToResult(request));
            }

            request.Approve(now);

            _eventCollector.Enqueue(new LateCheckoutApproved
            {
                TenantId = command.TenantId,
                AggregateId = request.Id,
                AggregateType = "LateCheckoutRequest",
                CorrelationId = Guid.NewGuid(),
                ActorType = "System",
                ReservationId = command.ReservationId,
                ApprovedCheckOutAt = command.RequestedCheckOutAt,
                UpdatesCleaning = policy.UpdatesCleaning,
            });

            _logger.LogInformation(
                "Late checkout approved for tenant {TenantId} reservationId {ReservationId}",
                command.TenantId, command.ReservationId);

            return Result.Success(ToResult(request));
        }, cancellationToken);

    private LateCheckoutRequestResult Deny(LateCheckoutRequest request, LateCheckoutDenialReason reason, DateTimeOffset now, Guid tenantId)
    {
        request.Deny(reason, now);

        _eventCollector.Enqueue(new LateCheckoutDenied
        {
            TenantId = tenantId,
            AggregateId = request.Id,
            AggregateType = "LateCheckoutRequest",
            CorrelationId = Guid.NewGuid(),
            ActorType = "System",
            ReservationId = request.ReservationId,
            ReasonCode = LateCheckoutRequestStatusCodeMapper.ToCode(reason),
        });

        _logger.LogInformation(
            "Late checkout denied for tenant {TenantId} reservationId {ReservationId}: {Reason}",
            tenantId, request.ReservationId, reason);

        return ToResult(request);
    }

    private static DomainChargeType ToDomainChargeType(ConfigurationChargeType chargeType) => chargeType switch
    {
        ConfigurationChargeType.None => DomainChargeType.None,
        ConfigurationChargeType.FixedAmount => DomainChargeType.FixedAmount,
        ConfigurationChargeType.Percentage => DomainChargeType.Percentage,
        _ => throw new ArgumentOutOfRangeException(nameof(chargeType), chargeType, "Unmapped Configuration.Contracts.LateCheckoutChargeType."),
    };

    private static LateCheckoutRequestResult ToResult(LateCheckoutRequest request) => new(
        request.Id,
        request.ReservationId,
        request.RequestedCheckOutAt,
        LateCheckoutRequestStatusCodeMapper.ToCode(request.ChargeType),
        request.ChargeValue,
        request.RequiresPix,
        LateCheckoutRequestStatusCodeMapper.ToCode(request.Status),
        request.DenialReason is { } reason ? LateCheckoutRequestStatusCodeMapper.ToCode(reason) : null,
        request.CreatedAtUtc,
        request.DecidedAtUtc,
        request.UpdatedAtUtc);
}
