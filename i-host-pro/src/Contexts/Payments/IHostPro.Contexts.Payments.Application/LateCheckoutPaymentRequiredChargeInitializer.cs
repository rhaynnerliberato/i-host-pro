using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.ExternalIntegrations.Contracts;
using IHostPro.Contexts.GuestOperations.Contracts;
using IHostPro.Contexts.Payments.Contracts;
using IHostPro.Contexts.Payments.Domain;
using Microsoft.Extensions.Logging;

namespace IHostPro.Contexts.Payments.Application;

/// <summary>
/// Reacts to <see cref="LateCheckoutPaymentRequired"/> (Fase 10, Checkpoint
/// 5 — PIX/Payment Deterministic Foundation): creates a new
/// <see cref="PixCharge"/>, calls the (fake, this checkpoint)
/// <see cref="IPixProvider"/> synchronously (ADR-025, exception #10), and —
/// only when the provider accepts — publishes <see cref="PixChargeCreated"/>.
///
/// Idempotent by construction: looks up an existing ACTIVE charge for the
/// same <c>LateCheckoutRequestId</c> before creating (mandate item 15) — a
/// redelivered <see cref="LateCheckoutPaymentRequired"/> never creates a
/// second <see cref="PixCharge"/>. The database's own partial unique index
/// remains defense-in-depth, never the primary idempotency mechanism.
///
/// When the provider rejects (or the call technically fails), the charge is
/// created as <c>Failed</c> — no <see cref="PixChargeCreated"/> is
/// published, and <see cref="LateCheckoutRequest"/> is left untouched
/// (remains <c>PendingPayment</c> — mandate items 11/14: a failed charge
/// does not deny the request; a new attempt would be a separate, explicit,
/// out-of-scope-for-this-checkpoint operation).
/// </summary>
public sealed class LateCheckoutPaymentRequiredChargeInitializer : IIntegrationEventHandler<LateCheckoutPaymentRequired>
{
    private const string AlreadyExistsReason = "AlreadyExists";

    private readonly IPixChargeReader _reader;
    private readonly IRepository<PixCharge, Guid> _repository;
    private readonly IPixProvider _pixProvider;
    private readonly IIntegrationEventCollector _eventCollector;
    private readonly IPaymentsTransactionExecutor _transactionExecutor;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<LateCheckoutPaymentRequiredChargeInitializer> _logger;

    public LateCheckoutPaymentRequiredChargeInitializer(
        IPixChargeReader reader,
        IRepository<PixCharge, Guid> repository,
        IPixProvider pixProvider,
        IIntegrationEventCollector eventCollector,
        IPaymentsTransactionExecutor transactionExecutor,
        TimeProvider timeProvider,
        ILogger<LateCheckoutPaymentRequiredChargeInitializer> logger)
    {
        _reader = reader;
        _repository = repository;
        _pixProvider = pixProvider;
        _eventCollector = eventCollector;
        _transactionExecutor = transactionExecutor;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public Task HandleAsync(LateCheckoutPaymentRequired @event, CancellationToken cancellationToken) =>
        _transactionExecutor.ExecuteAsync(async () =>
        {
            var existingId = await _reader.GetActiveIdByLateCheckoutRequestIdAsync(@event.LateCheckoutRequestId, cancellationToken);

            if (existingId is not null)
            {
                _logger.LogInformation(
                    "PixCharge initialization no-op for tenant {TenantId} lateCheckoutRequestId {LateCheckoutRequestId}: {Result}",
                    @event.TenantId, @event.LateCheckoutRequestId, AlreadyExistsReason);
                return true;
            }

            var now = _timeProvider.GetUtcNow();
            var chargeId = Guid.NewGuid();

            var charge = PixCharge.Create(
                chargeId, @event.TenantId, @event.LateCheckoutRequestId, @event.ReservationId,
                @event.Amount, @event.CurrencyCode, now);
            _repository.Add(charge);

            var providerResult = await _pixProvider.CreateChargeAsync(
                new PixChargeRequest(@event.TenantId, chargeId, charge.IdempotencyKey, @event.Amount, @event.CurrencyCode),
                cancellationToken);

            if (!providerResult.Accepted || providerResult.ProviderChargeId is null || providerResult.QrCodePayload is null)
            {
                charge.Fail(now);

                _logger.LogWarning(
                    "PixCharge rejected by provider for tenant {TenantId} lateCheckoutRequestId {LateCheckoutRequestId}: {FailureCode}",
                    @event.TenantId, @event.LateCheckoutRequestId, providerResult.FailureCode);

                return true;
            }

            charge.RecordProviderAcceptance(providerResult.ProviderChargeId, providerResult.QrCodePayload, providerResult.ExpiresAtUtc, now);

            _eventCollector.Enqueue(new PixChargeCreated
            {
                TenantId = @event.TenantId,
                AggregateId = chargeId,
                AggregateType = "PixCharge",
                CorrelationId = @event.CorrelationId,
                CausationId = @event.EventId,
                ActorType = "System",
                LateCheckoutRequestId = @event.LateCheckoutRequestId,
                ReservationId = @event.ReservationId,
            });

            _logger.LogInformation(
                "PixCharge created for tenant {TenantId} lateCheckoutRequestId {LateCheckoutRequestId} pixChargeId {PixChargeId}",
                @event.TenantId, @event.LateCheckoutRequestId, chargeId);

            return true;
        }, cancellationToken);
}
