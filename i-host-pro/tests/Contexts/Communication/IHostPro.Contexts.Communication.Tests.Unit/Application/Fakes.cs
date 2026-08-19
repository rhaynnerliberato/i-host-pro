using IHostPro.Contexts.Communication.Application;
using IHostPro.Contexts.Communication.Domain;
using IHostPro.Contexts.Configuration.Contracts;
using IHostPro.Contexts.Reservations.Contracts;

namespace IHostPro.Contexts.Communication.Tests.Unit.Application;

internal sealed class FakeTemplateReader : ITemplateReader
{
    private readonly ActiveTemplate? _template;

    private FakeTemplateReader(ActiveTemplate? template) => _template = template;

    public static FakeTemplateReader Returning(ActiveTemplate? template) => new(template);

    public Task<ActiveTemplate?> GetActiveByKeyAsync(Guid tenantId, string key, CancellationToken cancellationToken) =>
        Task.FromResult(_template);
}

internal sealed class FakeReservationGuestContactReader : IReservationGuestContactReader
{
    private readonly ReservationGuestContact? _contact;

    private FakeReservationGuestContactReader(ReservationGuestContact? contact) => _contact = contact;

    public static FakeReservationGuestContactReader Returning(ReservationGuestContact? contact) => new(contact);

    public Task<ReservationGuestContact?> GetGuestContactAsync(Guid tenantId, Guid reservationId, CancellationToken cancellationToken) =>
        Task.FromResult(_contact);
}

internal sealed class FakeMessageRepository : IMessageRepository
{
    private readonly Dictionary<string, Message> _byIdempotencyKey = new();
    private readonly Dictionary<Guid, Message> _byId = new();

    public static FakeMessageRepository WithExisting(Message? existing)
    {
        var repository = new FakeMessageRepository();
        if (existing is not null)
        {
            repository._byIdempotencyKey[existing.IdempotencyKey] = existing;
            repository._byId[existing.Id] = existing;
        }
        return repository;
    }

    public List<Message> AddedMessages { get; } = [];
    public List<Message> UpdatedMessages { get; } = [];

    public Task<Message?> GetByIdempotencyKeyAsync(string idempotencyKey, CancellationToken cancellationToken) =>
        Task.FromResult(_byIdempotencyKey.GetValueOrDefault(idempotencyKey));

    public Task<Message?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_byId.GetValueOrDefault(id));

    public void Add(Message aggregate)
    {
        _byIdempotencyKey[aggregate.IdempotencyKey] = aggregate;
        _byId[aggregate.Id] = aggregate;
        AddedMessages.Add(aggregate);
    }

    public void Update(Message aggregate)
    {
        _byIdempotencyKey[aggregate.IdempotencyKey] = aggregate;
        _byId[aggregate.Id] = aggregate;
        UpdatedMessages.Add(aggregate);
    }

    public void Remove(Message aggregate) => _byIdempotencyKey.Remove(aggregate.IdempotencyKey);
}

/// <summary>Runs the operation directly — no real transaction/RLS needed for these fast unit tests (that guarantee is covered by the real-Postgres Integration suite).</summary>
internal sealed class PassThroughCommunicationTransactionExecutor : ICommunicationTransactionExecutor
{
    public Task<TResponse> ExecuteAsync<TResponse>(Func<Task<TResponse>> operation, CancellationToken cancellationToken) =>
        operation();
}

internal sealed class FakeOutboundMessageConnector : IOutboundMessageConnector
{
    private readonly Func<OutboundMessageDispatch, OutboundMessageDispatchResult> _behavior;

    private FakeOutboundMessageConnector(Func<OutboundMessageDispatch, OutboundMessageDispatchResult> behavior) =>
        _behavior = behavior;

    public static FakeOutboundMessageConnector Succeeding() =>
        new(_ => new OutboundMessageDispatchResult(Success: true, FailureReason: null));

    public static FakeOutboundMessageConnector Rejecting(string failureReason) =>
        new(_ => new OutboundMessageDispatchResult(Success: false, FailureReason: failureReason));

    public static FakeOutboundMessageConnector Throwing(Exception exception) =>
        new(_ => throw exception);

    public List<OutboundMessageDispatch> ReceivedDispatches { get; } = [];

    public Task<OutboundMessageDispatchResult> SendAsync(OutboundMessageDispatch dispatch, CancellationToken cancellationToken)
    {
        ReceivedDispatches.Add(dispatch);
        return Task.FromResult(_behavior(dispatch));
    }
}
