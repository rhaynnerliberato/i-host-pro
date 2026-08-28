using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.Payments.Contracts;
using IHostPro.Contexts.Payments.Domain;
using Microsoft.Extensions.Logging;

namespace IHostPro.Contexts.Payments.Application;

/// <summary>
/// Applies <see cref="PixChargeConfirmationReceived"/> to the identified
/// <see cref="PixCharge"/> (Fase 10, Checkpoint 5 — PIX/Payment
/// Deterministic Foundation): calls <see cref="PixCharge.Confirm"/> — which
/// itself already applies the full approved transition matrix (mandate item
/// 10, idempotent duplicate-confirmation no-op included) — and, only on a
/// genuine (non-duplicate) transition to <c>Confirmed</c>, publishes
/// <see cref="PixChargeConfirmed"/>.
///
/// A message for an unknown <c>PixChargeId</c> (wrong tenant or truly
/// nonexistent) is logged and dropped — never an exception, since nothing
/// about this input is retriable-in-a-useful-way (mirrors the "unknown id
/// and cross-tenant id are indistinguishable" convention used by every
/// synchronous reader in this platform).
/// </summary>
public sealed class PixChargeConfirmationReceivedCommandHandler : IPixChargeConfirmationReceivedHandler
{
    private readonly IRepository<PixCharge, Guid> _repository;
    private readonly IIntegrationEventCollector _eventCollector;
    private readonly IPaymentsTransactionExecutor _transactionExecutor;
    private readonly ILogger<PixChargeConfirmationReceivedCommandHandler> _logger;

    public PixChargeConfirmationReceivedCommandHandler(
        IRepository<PixCharge, Guid> repository,
        IIntegrationEventCollector eventCollector,
        IPaymentsTransactionExecutor transactionExecutor,
        ILogger<PixChargeConfirmationReceivedCommandHandler> logger)
    {
        _repository = repository;
        _eventCollector = eventCollector;
        _transactionExecutor = transactionExecutor;
        _logger = logger;
    }

    public Task HandleAsync(PixChargeConfirmationReceived message, CancellationToken cancellationToken) =>
        _transactionExecutor.ExecuteAsync(async () =>
        {
            var charge = await _repository.GetByIdAsync(message.PixChargeId, cancellationToken);

            if (charge is null || charge.TenantId != message.TenantId)
            {
                _logger.LogWarning(
                    "PixChargeConfirmationReceived dropped for tenant {TenantId} pixChargeId {PixChargeId}: {Result}",
                    message.TenantId, message.PixChargeId, "NotFound");
                return true;
            }

            var wasAlreadyConfirmed = charge.Status == Domain.Enums.PixChargeStatus.Confirmed;

            charge.Confirm(message.ConfirmedAtUtc);
            _repository.Update(charge);

            if (wasAlreadyConfirmed)
            {
                _logger.LogInformation(
                    "PixChargeConfirmationReceived no-op (already confirmed) for tenant {TenantId} pixChargeId {PixChargeId}",
                    message.TenantId, message.PixChargeId);
                return true;
            }

            _eventCollector.Enqueue(new PixChargeConfirmed
            {
                TenantId = charge.TenantId,
                AggregateId = charge.Id,
                AggregateType = "PixCharge",
                CorrelationId = message.CorrelationId,
                CausationId = message.CausationId,
                ActorType = "System",
                LateCheckoutRequestId = charge.LateCheckoutRequestId,
                ReservationId = charge.ReservationId,
                ConfirmedAtUtc = message.ConfirmedAtUtc,
            });

            _logger.LogInformation(
                "PixCharge confirmed for tenant {TenantId} pixChargeId {PixChargeId} lateCheckoutRequestId {LateCheckoutRequestId}",
                message.TenantId, message.PixChargeId, charge.LateCheckoutRequestId);

            return true;
        }, cancellationToken);
}
