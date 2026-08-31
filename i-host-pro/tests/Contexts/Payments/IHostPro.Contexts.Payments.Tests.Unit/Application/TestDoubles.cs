using IHostPro.BuildingBlocks.Domain;
using IHostPro.BuildingBlocks.Messaging.Abstractions;
using IHostPro.Contexts.ExternalIntegrations.Contracts;
using IHostPro.Contexts.Payments.Application;
using IHostPro.Contexts.Payments.Domain;

namespace IHostPro.Contexts.Payments.Tests.Unit.Application;

/// <summary>Hand-written test double — mirrors GuestOperations.Tests.Unit's own RecordingLateCheckoutRequestRepository.</summary>
internal sealed class RecordingPixChargeRepository : IRepository<PixCharge, Guid>
{
    public List<PixCharge> AddedCharges { get; } = [];
    public int UpdateCallCount { get; private set; }

    public Task<PixCharge?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(AddedCharges.FirstOrDefault(c => c.Id == id));

    public void Add(PixCharge aggregate) => AddedCharges.Add(aggregate);

    public void Update(PixCharge aggregate) => UpdateCallCount++;

    public void Remove(PixCharge aggregate) => throw new NotSupportedException("A PixCharge is never deleted.");
}

internal sealed class FakePixChargeReader : IPixChargeReader
{
    private readonly Guid? _activeChargeId;

    private FakePixChargeReader(Guid? activeChargeId) => _activeChargeId = activeChargeId;

    public static FakePixChargeReader WithActiveCharge(Guid? activeChargeId) => new(activeChargeId);

    public Task<Guid?> GetActiveIdByLateCheckoutRequestIdAsync(Guid lateCheckoutRequestId, CancellationToken cancellationToken) =>
        Task.FromResult(_activeChargeId);

    public PaymentStatusResult? StatusResult { get; set; }

    public Task<PaymentStatusResult?> GetStatusByReservationIdAsync(Guid reservationId, CancellationToken cancellationToken) =>
        Task.FromResult(StatusResult);
}

internal sealed class FakeIntegrationEventCollector : IIntegrationEventCollector
{
    public List<IntegrationEvent> EnqueuedEvents { get; } = [];

    public void Enqueue(IntegrationEvent @event) => EnqueuedEvents.Add(@event);

    public IReadOnlyList<IntegrationEvent> Drain()
    {
        var drained = EnqueuedEvents.ToArray();
        EnqueuedEvents.Clear();
        return drained;
    }
}

/// <summary>No real transaction/outbox — this unit test exercises handler logic only; the real executor is covered by the integration/E2E suite.</summary>
internal sealed class PassThroughPaymentsTransactionExecutor : IPaymentsTransactionExecutor
{
    public Task<TResponse> ExecuteAsync<TResponse>(
        Func<Task<TResponse>> operation, CancellationToken cancellationToken) => operation();
}

internal sealed class FixedTimeProvider : TimeProvider
{
    private readonly DateTimeOffset _now;

    public FixedTimeProvider(DateTimeOffset now) => _now = now;

    public override DateTimeOffset GetUtcNow() => _now;
}

/// <summary>
/// Configurable double for <see cref="IPixProvider"/> — unlike the
/// production <c>FakePixProvider</c> (ExternalIntegrations.Infrastructure,
/// which always accepts deterministically), this one lets a unit test
/// exercise the Accepted/Rejected/technical-failure branches of
/// <c>LateCheckoutPaymentRequiredChargeInitializer</c> without any network
/// dependency.
/// </summary>
internal sealed class ConfigurablePixProvider : IPixProvider
{
    private readonly PixChargeCreationResult? _result;
    private readonly Exception? _exceptionToThrow;

    private ConfigurablePixProvider(PixChargeCreationResult? result, Exception? exceptionToThrow)
    {
        _result = result;
        _exceptionToThrow = exceptionToThrow;
    }

    public static ConfigurablePixProvider Accepting(string providerChargeId, string qrCodePayload, DateTimeOffset? expiresAtUtc) =>
        new(new PixChargeCreationResult(true, providerChargeId, qrCodePayload, expiresAtUtc, null), null);

    public static ConfigurablePixProvider Rejecting(string failureCode) =>
        new(new PixChargeCreationResult(false, null, null, null, failureCode), null);

    public static ConfigurablePixProvider ThrowingTechnicalFailure(Exception exception) =>
        new(null, exception);

    public int CallCount { get; private set; }

    public Task<PixChargeCreationResult> CreateChargeAsync(PixChargeRequest request, CancellationToken cancellationToken)
    {
        CallCount++;

        if (_exceptionToThrow is not null)
            throw _exceptionToThrow;

        return Task.FromResult(_result!);
    }
}
