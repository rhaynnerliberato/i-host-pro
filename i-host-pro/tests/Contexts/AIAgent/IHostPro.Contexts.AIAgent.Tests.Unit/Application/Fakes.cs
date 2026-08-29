using IHostPro.Contexts.AIAgent.Application;
using IHostPro.Contexts.AIAgent.Domain;

namespace IHostPro.Contexts.AIAgent.Tests.Unit.Application;

internal sealed class FakeAgentSessionResolver : IAgentSessionResolver
{
    private readonly Guid _sessionId;

    private FakeAgentSessionResolver(Guid sessionId) => _sessionId = sessionId;

    public static FakeAgentSessionResolver Returning(Guid sessionId) => new(sessionId);

    public Task<Guid> GetOrCreateActiveSessionIdAsync(
        Guid tenantId, Guid conversationId, Guid reservationId, DateTimeOffset occurredAtUtc, CancellationToken cancellationToken) =>
        Task.FromResult(_sessionId);
}

internal sealed class FakeAgentSessionRepository : IAgentSessionRepository
{
    private readonly Dictionary<Guid, AgentSession> _byId = new();

    public static FakeAgentSessionRepository WithExisting(AgentSession session)
    {
        var repository = new FakeAgentSessionRepository();
        repository._byId[session.Id] = session;
        return repository;
    }

    public List<AgentSession> UpdatedSessions { get; } = [];

    public Task<AgentSession?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_byId.GetValueOrDefault(id));

    public Task<AgentSession?> GetActiveByConversationIdAsync(Guid conversationId, CancellationToken cancellationToken) =>
        Task.FromResult(_byId.Values.SingleOrDefault(s => s.ConversationId == conversationId && s.Status == AgentSessionStatus.Active));

    public void Add(AgentSession aggregate) => _byId[aggregate.Id] = aggregate;

    public void Update(AgentSession aggregate)
    {
        _byId[aggregate.Id] = aggregate;
        UpdatedSessions.Add(aggregate);
    }

    public void Remove(AgentSession aggregate) => _byId.Remove(aggregate.Id);
}

internal sealed class FakeAgentInteractionRepository : IAgentInteractionRepository
{
    private readonly Dictionary<Guid, AgentInteraction> _byId = new();

    public static FakeAgentInteractionRepository WithExisting(AgentInteraction? existing)
    {
        var repository = new FakeAgentInteractionRepository();
        if (existing is not null)
            repository._byId[existing.Id] = existing;
        return repository;
    }

    public List<AgentInteraction> AddedInteractions { get; } = [];

    public Task<AgentInteraction?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_byId.GetValueOrDefault(id));

    public Task<AgentInteraction?> GetByInboundMessageIdAsync(Guid tenantId, Guid inboundMessageId, CancellationToken cancellationToken) =>
        Task.FromResult(_byId.Values.SingleOrDefault(i => i.TenantId == tenantId && i.InboundMessageId == inboundMessageId));

    public void Add(AgentInteraction aggregate)
    {
        _byId[aggregate.Id] = aggregate;
        AddedInteractions.Add(aggregate);
    }

    public void Update(AgentInteraction aggregate) => _byId[aggregate.Id] = aggregate;

    public void Remove(AgentInteraction aggregate) => _byId.Remove(aggregate.Id);
}

internal sealed class FakeAgentContextBuilder : IAgentContextBuilder
{
    private readonly ModelRequest _request;

    private FakeAgentContextBuilder(ModelRequest request) => _request = request;

    public static FakeAgentContextBuilder Returning(ModelRequest request) => new(request);

    public Task<ModelRequest> BuildAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken) =>
        Task.FromResult(_request);
}

/// <summary>Runs the operation directly — no real transaction/RLS needed for these fast unit tests (that guarantee is covered by the real-Postgres Integration suite).</summary>
internal sealed class PassThroughAIAgentTransactionExecutor : IAIAgentTransactionExecutor
{
    public Task<TResponse> ExecuteAsync<TResponse>(Func<Task<TResponse>> operation, CancellationToken cancellationToken) =>
        operation();
}
