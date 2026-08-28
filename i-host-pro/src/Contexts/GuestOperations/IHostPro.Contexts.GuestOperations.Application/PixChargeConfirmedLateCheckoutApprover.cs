using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.Configuration.Contracts;
using IHostPro.Contexts.GuestOperations.Domain;
using IHostPro.Contexts.GuestOperations.Domain.Enums;
using IHostPro.Contexts.Payments.Contracts;
using Microsoft.Extensions.Logging;

namespace IHostPro.Contexts.GuestOperations.Application;

/// <summary>
/// Reacts to <see cref="PixChargeConfirmed"/> (Fase 10, Checkpoint 5 —
/// PIX/Payment Deterministic Foundation; choreography, async boundary):
/// looks up the <see cref="LateCheckoutRequest"/> by
/// <see cref="PixChargeConfirmed.LateCheckoutRequestId"/>, and — only when
/// it is still <see cref="LateCheckoutRequestStatus.PendingPayment"/> —
/// calls <see cref="LateCheckoutRequest.Approve"/>, publishing the EXISTING
/// <see cref="Contracts.LateCheckoutApproved"/> event. Reuses the
/// Checkpoint 3 approval path exactly — no new approval logic is
/// duplicated here ("Fluxo CP3 continua", mandate item 31).
///
/// <see cref="Contracts.LateCheckoutApproved.UpdatesCleaning"/> must be a
/// snapshot of the resolved <c>LateCheckoutPolicy.UpdatesCleaning</c> — the
/// SAME source of truth <see cref="RequestLateCheckoutCommandHandler"/>
/// itself reads, since <c>LateCheckoutRequest</c> does not persist this
/// flag (it only snapshots ChargeType/ChargeValue/RequiresPix). This
/// handler therefore re-reads <see cref="ILateCheckoutPolicyReader"/> at
/// confirmation time — the same approved synchronous exception (general
/// Configuration &amp; Policy exception, ADR-002) GuestOperations already
/// holds, never a new one. A small TOCTOU window is accepted here (the
/// policy could theoretically change between the original request and this
/// later confirmation) — same accepted-risk precedent already documented
/// for every synchronous cross-context read in this platform (ADR-014/019).
/// If the policy can no longer be resolved at all, this defaults to
/// <see langword="false"/> (never triggering Housekeeping) rather than
/// guessing — a conservative, no-side-effect default.
///
/// Idempotent by construction (mandate item 32): if the request is already
/// <see cref="LateCheckoutRequestStatus.Approved"/> (a duplicate
/// <see cref="PixChargeConfirmed"/> delivery — <c>PixCharge.Confirm</c> is
/// itself idempotent and only publishes once, but Wolverine redelivery of
/// the SAME message is still possible), this is a no-op: no second
/// <see cref="Contracts.LateCheckoutApproved"/> is ever published.
/// </summary>
public sealed class PixChargeConfirmedLateCheckoutApprover : IIntegrationEventHandler<PixChargeConfirmed>
{
    private readonly IRepository<LateCheckoutRequest, Guid> _repository;
    private readonly ILateCheckoutPolicyReader _policyReader;
    private readonly IIntegrationEventCollector _eventCollector;
    private readonly IGuestOperationsTransactionExecutor _transactionExecutor;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<PixChargeConfirmedLateCheckoutApprover> _logger;

    public PixChargeConfirmedLateCheckoutApprover(
        IRepository<LateCheckoutRequest, Guid> repository,
        ILateCheckoutPolicyReader policyReader,
        IIntegrationEventCollector eventCollector,
        IGuestOperationsTransactionExecutor transactionExecutor,
        TimeProvider timeProvider,
        ILogger<PixChargeConfirmedLateCheckoutApprover> logger)
    {
        _repository = repository;
        _policyReader = policyReader;
        _eventCollector = eventCollector;
        _transactionExecutor = transactionExecutor;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public Task HandleAsync(PixChargeConfirmed @event, CancellationToken cancellationToken) =>
        _transactionExecutor.ExecuteAsync(async () =>
        {
            var request = await _repository.GetByIdAsync(@event.LateCheckoutRequestId, cancellationToken);

            if (request is null || request.TenantId != @event.TenantId)
            {
                _logger.LogWarning(
                    "PixChargeConfirmed dropped for tenant {TenantId} lateCheckoutRequestId {LateCheckoutRequestId}: {Result}",
                    @event.TenantId, @event.LateCheckoutRequestId, "LateCheckoutRequestNotFound");
                return true;
            }

            if (request.Status != LateCheckoutRequestStatus.PendingPayment)
            {
                _logger.LogInformation(
                    "PixChargeConfirmed no-op for tenant {TenantId} lateCheckoutRequestId {LateCheckoutRequestId}: {Result} (status {Status})",
                    @event.TenantId, @event.LateCheckoutRequestId, "NotPendingPayment", request.Status);
                return true;
            }

            var policyResult = await _policyReader.GetEffectiveAsync(@event.TenantId, request.PropertyId, cancellationToken);
            var updatesCleaning = policyResult is { Status: PolicyReadStatus.Resolved, Value.UpdatesCleaning: true };

            var now = _timeProvider.GetUtcNow();
            request.Approve(now);
            _repository.Update(request);

            _eventCollector.Enqueue(new Contracts.LateCheckoutApproved
            {
                TenantId = @event.TenantId,
                AggregateId = request.Id,
                AggregateType = "LateCheckoutRequest",
                CorrelationId = @event.CorrelationId,
                CausationId = @event.EventId,
                ActorType = "System",
                ReservationId = request.ReservationId,
                PropertyId = request.PropertyId,
                ApprovedCheckOutAt = request.RequestedCheckOutAt,
                UpdatesCleaning = updatesCleaning,
            });

            _logger.LogInformation(
                "Late checkout approved via PIX confirmation for tenant {TenantId} reservationId {ReservationId}",
                @event.TenantId, request.ReservationId);

            return true;
        }, cancellationToken);
}
