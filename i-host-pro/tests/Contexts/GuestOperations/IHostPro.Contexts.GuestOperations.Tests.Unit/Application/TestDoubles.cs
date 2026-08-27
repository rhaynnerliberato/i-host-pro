using IHostPro.BuildingBlocks.Domain;
using IHostPro.BuildingBlocks.Messaging.Abstractions;
using IHostPro.Contexts.GuestOperations.Application;
using IHostPro.Contexts.GuestOperations.Domain;

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
