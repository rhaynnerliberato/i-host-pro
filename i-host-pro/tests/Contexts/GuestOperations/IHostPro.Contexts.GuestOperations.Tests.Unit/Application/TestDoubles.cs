using IHostPro.BuildingBlocks.Domain;
using IHostPro.BuildingBlocks.Messaging.Abstractions;
using IHostPro.Contexts.Configuration.Contracts;
using IHostPro.Contexts.GuestOperations.Application;
using IHostPro.Contexts.GuestOperations.Domain;
using IHostPro.Contexts.Housekeeping.Contracts;
using IHostPro.Contexts.Reservations.Contracts;

namespace IHostPro.Contexts.GuestOperations.Tests.Unit.Application;

/// <summary>Hand-written test double — mirrors Reservations.Tests.Unit's own RecordingReservationRepository.</summary>
internal sealed class RecordingGuestStayOperationRepository : IRepository<GuestStayOperation, Guid>
{
    private readonly GuestStayOperation? _operation;

    private RecordingGuestStayOperationRepository(GuestStayOperation? operation) => _operation = operation;

    public static RecordingGuestStayOperationRepository WithOperation(GuestStayOperation? operation) => new(operation);

    public int UpdateCallCount { get; private set; }
    public int AddCallCount { get; private set; }

    public Task<GuestStayOperation?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_operation is not null && _operation.Id == id ? _operation : null);

    public void Add(GuestStayOperation aggregate) => AddCallCount++;

    public void Update(GuestStayOperation aggregate) => UpdateCallCount++;

    public void Remove(GuestStayOperation aggregate) => throw new NotSupportedException("No removal path exists in this checkpoint.");
}

internal sealed class FakeGuestStayOperationReader : IGuestStayOperationReader
{
    private readonly Guid? _operationId;

    private FakeGuestStayOperationReader(Guid? operationId) => _operationId = operationId;

    public static FakeGuestStayOperationReader WithOperationIdResult(Guid? operationId) => new(operationId);

    public Task<Guid?> GetIdByReservationIdAsync(Guid reservationId, CancellationToken cancellationToken) =>
        Task.FromResult(_operationId);
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
internal sealed class PassThroughGuestOperationsTransactionExecutor : IGuestOperationsTransactionExecutor
{
    public Task<TResponse> ExecuteAsync<TResponse>(
        Func<Task<TResponse>> operation, CancellationToken cancellationToken) => operation();
}

/// <summary>Minimal deterministic <see cref="TimeProvider"/> test double — always returns a fixed instant.</summary>
internal sealed class FixedTimeProvider : TimeProvider
{
    private readonly DateTimeOffset _now;

    public FixedTimeProvider(DateTimeOffset now) => _now = now;

    public override DateTimeOffset GetUtcNow() => _now;
}

// ---- Fase 10, Checkpoint 3 — Early Check-in / Late Checkout test doubles ----

internal sealed class RecordingEarlyCheckInRequestRepository : IRepository<EarlyCheckInRequest, Guid>
{
    public List<EarlyCheckInRequest> AddedRequests { get; } = [];
    public int UpdateCallCount { get; private set; }

    public Task<EarlyCheckInRequest?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(AddedRequests.FirstOrDefault(r => r.Id == id));

    public void Add(EarlyCheckInRequest aggregate) => AddedRequests.Add(aggregate);

    public void Update(EarlyCheckInRequest aggregate) => UpdateCallCount++;

    public void Remove(EarlyCheckInRequest aggregate) => throw new NotSupportedException("No removal path exists in this checkpoint.");
}

internal sealed class RecordingLateCheckoutRequestRepository : IRepository<LateCheckoutRequest, Guid>
{
    public List<LateCheckoutRequest> AddedRequests { get; } = [];
    public int UpdateCallCount { get; private set; }

    public Task<LateCheckoutRequest?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(AddedRequests.FirstOrDefault(r => r.Id == id));

    public void Add(LateCheckoutRequest aggregate) => AddedRequests.Add(aggregate);

    public void Update(LateCheckoutRequest aggregate) => UpdateCallCount++;

    public void Remove(LateCheckoutRequest aggregate) => throw new NotSupportedException("No removal path exists in this checkpoint.");
}

internal sealed class FakeEarlyCheckInRequestReader : IEarlyCheckInRequestReader
{
    private readonly bool _hasActiveRequest;

    private FakeEarlyCheckInRequestReader(bool hasActiveRequest) => _hasActiveRequest = hasActiveRequest;

    public static FakeEarlyCheckInRequestReader WithActiveRequest(bool hasActiveRequest) => new(hasActiveRequest);

    public Task<bool> HasActiveRequestAsync(Guid reservationId, CancellationToken cancellationToken) =>
        Task.FromResult(_hasActiveRequest);
}

internal sealed class FakeLateCheckoutRequestReader : ILateCheckoutRequestReader
{
    private readonly bool _hasActiveRequest;

    private FakeLateCheckoutRequestReader(bool hasActiveRequest) => _hasActiveRequest = hasActiveRequest;

    public static FakeLateCheckoutRequestReader WithActiveRequest(bool hasActiveRequest) => new(hasActiveRequest);

    public Task<bool> HasActiveRequestAsync(Guid reservationId, CancellationToken cancellationToken) =>
        Task.FromResult(_hasActiveRequest);
}

internal sealed class FakeReservationScheduleReader : IReservationScheduleReader
{
    private readonly ReservationScheduleSnapshot? _schedule;
    private readonly bool _hasConflict;

    private FakeReservationScheduleReader(ReservationScheduleSnapshot? schedule, bool hasConflict)
    {
        _schedule = schedule;
        _hasConflict = hasConflict;
    }

    public static FakeReservationScheduleReader WithSchedule(ReservationScheduleSnapshot? schedule, bool hasConflict = false) =>
        new(schedule, hasConflict);

    public Task<ReservationScheduleSnapshot?> GetScheduleAsync(Guid tenantId, Guid reservationId, CancellationToken cancellationToken) =>
        Task.FromResult(_schedule);

    public Task<bool> HasConflictingReservationAsync(
        Guid tenantId, Guid reservationId, DateTimeOffset requestedCheckInAt, DateTimeOffset requestedCheckOutAt,
        CancellationToken cancellationToken) =>
        Task.FromResult(_hasConflict);
}

internal sealed class FakeCleaningReadinessReader : ICleaningReadinessReader
{
    private readonly bool _isCompleted;

    private FakeCleaningReadinessReader(bool isCompleted) => _isCompleted = isCompleted;

    public static FakeCleaningReadinessReader WithCompleted(bool isCompleted) => new(isCompleted);

    public Task<bool> IsCleaningCompletedAsync(Guid tenantId, Guid reservationId, CancellationToken cancellationToken) =>
        Task.FromResult(_isCompleted);
}

internal sealed class FakeEarlyCheckInPolicyReader : IEarlyCheckInPolicyReader
{
    private readonly PolicyReadResult<EarlyCheckInPolicy> _result;

    private FakeEarlyCheckInPolicyReader(PolicyReadResult<EarlyCheckInPolicy> result) => _result = result;

    public static FakeEarlyCheckInPolicyReader WithResult(PolicyReadResult<EarlyCheckInPolicy> result) => new(result);

    public Task<PolicyReadResult<EarlyCheckInPolicy>> GetEffectiveAsync(
        Guid tenantId, Guid? propertyId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_result);
}

internal sealed class FakeLateCheckoutPolicyReader : ILateCheckoutPolicyReader
{
    private readonly PolicyReadResult<LateCheckoutPolicy> _result;

    private FakeLateCheckoutPolicyReader(PolicyReadResult<LateCheckoutPolicy> result) => _result = result;

    public static FakeLateCheckoutPolicyReader WithResult(PolicyReadResult<LateCheckoutPolicy> result) => new(result);

    public Task<PolicyReadResult<LateCheckoutPolicy>> GetEffectiveAsync(
        Guid tenantId, Guid? propertyId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_result);
}
