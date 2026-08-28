using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.Payments.Contracts;
using IHostPro.Contexts.Payments.Domain;
using Microsoft.Extensions.Logging;

namespace IHostPro.Contexts.Payments.Application;

/// <summary>
/// Applies <see cref="PixChargeFailureReceived"/> to the identified
/// <see cref="PixCharge"/> (Fase 10, Checkpoint 5.1 — Payment
/// Failure/Expiration Evidence Corrective Gate): calls
/// <see cref="PixCharge.Fail"/> — already idempotent (a real confirmation or
/// an already-settled terminal state always takes precedence over a late or
/// duplicate failure signal, mandate item 6). Publishes NOTHING downstream —
/// nothing in this checkpoint consumes a PixCharge Failed transition
/// (mandate item 5: "não publicar evento downstream se ninguém precisa
/// consumir"); <c>LateCheckoutRequest</c> is intentionally never touched
/// here, remaining <c>PendingPayment</c> (mandate item 6).
///
/// A message for an unknown <c>PixChargeId</c> (wrong tenant or truly
/// nonexistent) is logged and dropped — never an exception, mirrors
/// <see cref="PixChargeConfirmationReceivedCommandHandler"/>'s own
/// "unknown id and cross-tenant id are indistinguishable" convention.
/// </summary>
public sealed class PixChargeFailureReceivedCommandHandler : IPixChargeFailureReceivedHandler
{
    private readonly IRepository<PixCharge, Guid> _repository;
    private readonly IPaymentsTransactionExecutor _transactionExecutor;
    private readonly ILogger<PixChargeFailureReceivedCommandHandler> _logger;

    public PixChargeFailureReceivedCommandHandler(
        IRepository<PixCharge, Guid> repository,
        IPaymentsTransactionExecutor transactionExecutor,
        ILogger<PixChargeFailureReceivedCommandHandler> logger)
    {
        _repository = repository;
        _transactionExecutor = transactionExecutor;
        _logger = logger;
    }

    public Task HandleAsync(PixChargeFailureReceived message, CancellationToken cancellationToken) =>
        _transactionExecutor.ExecuteAsync(async () =>
        {
            var charge = await _repository.GetByIdAsync(message.PixChargeId, cancellationToken);

            if (charge is null || charge.TenantId != message.TenantId)
            {
                _logger.LogWarning(
                    "PixChargeFailureReceived dropped for tenant {TenantId} pixChargeId {PixChargeId}: {Result}",
                    message.TenantId, message.PixChargeId, "NotFound");
                return true;
            }

            charge.Fail(message.OccurredAtUtc);
            _repository.Update(charge);

            _logger.LogInformation(
                "PixChargeFailureReceived applied for tenant {TenantId} pixChargeId {PixChargeId} status {Status} failureCode {FailureCode}",
                message.TenantId, message.PixChargeId, charge.Status, message.FailureCode);

            return true;
        }, cancellationToken);
}
