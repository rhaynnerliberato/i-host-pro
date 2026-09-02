using IHostPro.Contexts.AIAgent.Application;
using IHostPro.Contexts.AIAgent.Application.Tools;
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

internal sealed class FakeAgentToolExecutionRepository : IAgentToolExecutionRepository
{
    private readonly Dictionary<Guid, AgentToolExecution> _byId = new();

    public List<AgentToolExecution> AddedExecutions { get; } = [];

    public Task<AgentToolExecution?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_byId.GetValueOrDefault(id));

    public void Add(AgentToolExecution aggregate)
    {
        _byId[aggregate.Id] = aggregate;
        AddedExecutions.Add(aggregate);
    }

    public void Update(AgentToolExecution aggregate) => _byId[aggregate.Id] = aggregate;

    public void Remove(AgentToolExecution aggregate) => _byId.Remove(aggregate.Id);
}

/// <summary>Fase 12, Checkpoint 3 — every existing flow-level test exercises AI Agent orchestration, never rate limiting, so this fake always allows.</summary>
internal sealed class FakeAiAgentRateLimiter : IAiAgentRateLimiter
{
    private readonly bool _allowed;
    private FakeAiAgentRateLimiter(bool allowed) => _allowed = allowed;
    public static FakeAiAgentRateLimiter AlwaysAllow() => new(true);
    public static FakeAiAgentRateLimiter AlwaysReject() => new(false);
    public Task<bool> IsAllowedAsync(Guid tenantId, CancellationToken cancellationToken) => Task.FromResult(_allowed);
}

internal sealed class FakeAgentContextBuilder : IAgentContextBuilder
{
    private readonly ModelRequest _request;

    private FakeAgentContextBuilder(ModelRequest request) => _request = request;

    public static FakeAgentContextBuilder Returning(ModelRequest request) => new(request);

    public Task<ModelRequest> BuildAsync(
        Guid tenantId, Guid conversationId, Guid triggeringInboundMessageId, Guid reservationId, CancellationToken cancellationToken) =>
        Task.FromResult(_request);
}

/// <summary>Runs the operation directly — no real transaction/RLS needed for these fast unit tests (that guarantee is covered by the real-Postgres Integration suite).</summary>
internal sealed class PassThroughAIAgentTransactionExecutor : IAIAgentTransactionExecutor
{
    public Task<TResponse> ExecuteAsync<TResponse>(Func<Task<TResponse>> operation, CancellationToken cancellationToken) =>
        operation();
}

/// <summary>Deterministic test double for the orchestrator's tool-calling loop (Fase 11, Checkpoint 3).</summary>
internal sealed class FakeAgentTool : IAgentTool
{
    private readonly AgentToolResult _result;
    private readonly Exception? _throws;

    private FakeAgentTool(string name, AgentToolResult result, Exception? throws)
    {
        Descriptor = new AgentToolDescriptor(name, "Fake tool for orchestrator tests.");
        _result = result;
        _throws = throws;
    }

    public static FakeAgentTool Succeeding(string name, string content) =>
        new(name, AgentToolResult.Success(content), null);

    public static FakeAgentTool Failing(string name, string failureCode) =>
        new(name, AgentToolResult.Failure(failureCode), null);

    public static FakeAgentTool Throwing(string name, Exception exception) =>
        new(name, AgentToolResult.Failure("unused"), exception);

    public AgentToolDescriptor Descriptor { get; }

    public AgentToolContext? LastContext { get; private set; }
    public IReadOnlyDictionary<string, string>? LastArguments { get; private set; }

    public Task<AgentToolResult> ExecuteAsync(
        AgentToolContext context, IReadOnlyDictionary<string, string>? arguments, CancellationToken cancellationToken)
    {
        LastContext = context;
        LastArguments = arguments;

        if (_throws is not null)
            throw _throws;

        return Task.FromResult(_result);
    }
}

/// <summary>Deterministic test double for the CP4 write-tool confirmation loop.</summary>
internal sealed class FakeConfirmableAgentTool : IConfirmableAgentTool
{
    private readonly AgentToolResult _executeResult;
    private readonly AgentPendingActionProposalResult _proposalResult;

    private FakeConfirmableAgentTool(string name, AgentPendingActionProposalResult proposalResult, AgentToolResult executeResult)
    {
        Descriptor = new AgentToolDescriptor(name, "Fake confirmable tool for orchestrator tests.");
        _proposalResult = proposalResult;
        _executeResult = executeResult;
    }

    public static FakeConfirmableAgentTool Succeeding(string name, string sanitizedArgumentsJson, string executeContent) =>
        new(name, AgentPendingActionProposalResult.Success(sanitizedArgumentsJson), AgentToolResult.Success(executeContent));

    public static FakeConfirmableAgentTool RejectingProposal(string name, string failureCode) =>
        new(name, AgentPendingActionProposalResult.Failure(failureCode), AgentToolResult.Failure("unused"));

    public static FakeConfirmableAgentTool FailingExecution(string name, string sanitizedArgumentsJson, string failureCode) =>
        new(name, AgentPendingActionProposalResult.Success(sanitizedArgumentsJson), AgentToolResult.Failure(failureCode));

    public AgentToolDescriptor Descriptor { get; }

    public IReadOnlyDictionary<string, string>? LastExecuteArguments { get; private set; }
    public int ExecuteCallCount { get; private set; }

    public AgentPendingActionProposalResult BuildSanitizedArguments(IReadOnlyDictionary<string, string>? arguments) => _proposalResult;

    public Task<AgentToolResult> ExecuteAsync(
        AgentToolContext context, IReadOnlyDictionary<string, string>? arguments, CancellationToken cancellationToken)
    {
        LastExecuteArguments = arguments;
        ExecuteCallCount++;
        return Task.FromResult(_executeResult);
    }
}

internal sealed class FakeAgentToolConfirmationPolicy : IAgentToolConfirmationPolicy
{
    private readonly IReadOnlySet<string> _confirmationRequiredToolNames;

    private FakeAgentToolConfirmationPolicy(IReadOnlySet<string> confirmationRequiredToolNames) =>
        _confirmationRequiredToolNames = confirmationRequiredToolNames;

    public static FakeAgentToolConfirmationPolicy RequiringConfirmationFor(params string[] toolNames) =>
        new(toolNames.ToHashSet());

    public static FakeAgentToolConfirmationPolicy RequiringNone() => new(new HashSet<string>());

    public bool RequiresConfirmation(string toolName) => _confirmationRequiredToolNames.Contains(toolName);
}

internal sealed class FakeAgentPendingActionRepository : IAgentPendingActionRepository
{
    private readonly Dictionary<Guid, AgentPendingAction> _byId = new();

    public static FakeAgentPendingActionRepository WithExisting(AgentPendingAction? existing)
    {
        var repository = new FakeAgentPendingActionRepository();
        if (existing is not null)
            repository._byId[existing.Id] = existing;
        return repository;
    }

    public List<AgentPendingAction> AddedPendingActions { get; } = [];
    public List<AgentPendingAction> UpdatedPendingActions { get; } = [];

    public void Add(AgentPendingAction pendingAction)
    {
        _byId[pendingAction.Id] = pendingAction;
        AddedPendingActions.Add(pendingAction);
    }

    public void Update(AgentPendingAction pendingAction)
    {
        _byId[pendingAction.Id] = pendingAction;
        UpdatedPendingActions.Add(pendingAction);
    }

    public Task<AgentPendingAction?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
        Task.FromResult(_byId.GetValueOrDefault(id));

    public Task<AgentPendingAction?> GetActiveByAgentSessionIdAsync(Guid agentSessionId, CancellationToken cancellationToken) =>
        Task.FromResult(_byId.Values.SingleOrDefault(a =>
            a.AgentSessionId == agentSessionId
            && (a.Status == AgentPendingActionStatus.Proposed || a.Status == AgentPendingActionStatus.Confirmed)));
}

internal sealed class FakeAgentHumanHandoffRepository : IAgentHumanHandoffRepository
{
    private readonly Dictionary<Guid, AgentHumanHandoff> _byId = new();

    public static FakeAgentHumanHandoffRepository WithExisting(AgentHumanHandoff? existing)
    {
        var repository = new FakeAgentHumanHandoffRepository();
        if (existing is not null)
            repository._byId[existing.Id] = existing;
        return repository;
    }

    public List<AgentHumanHandoff> AddedHandoffs { get; } = [];
    public List<AgentHumanHandoff> UpdatedHandoffs { get; } = [];

    public void Add(AgentHumanHandoff handoff)
    {
        _byId[handoff.Id] = handoff;
        AddedHandoffs.Add(handoff);
    }

    public void Update(AgentHumanHandoff handoff)
    {
        _byId[handoff.Id] = handoff;
        UpdatedHandoffs.Add(handoff);
    }

    public Task<AgentHumanHandoff?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
        Task.FromResult(_byId.GetValueOrDefault(id));

    public Task<AgentHumanHandoff?> GetActiveByAgentSessionIdAsync(Guid agentSessionId, CancellationToken cancellationToken) =>
        Task.FromResult(_byId.Values.SingleOrDefault(h =>
            h.AgentSessionId == agentSessionId
            && (h.Status == AgentHumanHandoffStatus.Requested || h.Status == AgentHumanHandoffStatus.Notified)));
}

internal sealed class FakeAdministratorNotificationService : IAdministratorNotificationService
{
    private readonly AdministratorNotificationResult _result;

    private FakeAdministratorNotificationService(AdministratorNotificationResult result) => _result = result;

    public static FakeAdministratorNotificationService Succeeding() => new(new AdministratorNotificationResult(true, null));

    public static FakeAdministratorNotificationService Failing(string failureCode) =>
        new(new AdministratorNotificationResult(false, failureCode));

    public List<(Guid TenantId, Guid ConversationId, Guid ReservationId, Guid AgentHumanHandoffId, string ReasonCode)> Calls { get; } = [];

    public Task<AdministratorNotificationResult> NotifyAsync(
        Guid tenantId, Guid conversationId, Guid reservationId, Guid agentHumanHandoffId, string reasonCode, CancellationToken cancellationToken)
    {
        Calls.Add((tenantId, conversationId, reservationId, agentHumanHandoffId, reasonCode));
        return Task.FromResult(_result);
    }
}

internal sealed class FakeAgentResponseDeliveryService : IAgentResponseDeliveryService
{
    private readonly AgentResponseDeliveryResult _result;

    private FakeAgentResponseDeliveryService(AgentResponseDeliveryResult result) => _result = result;

    public static FakeAgentResponseDeliveryService Succeeding(Guid messageId) =>
        new(new AgentResponseDeliveryResult(true, messageId, null));

    public static FakeAgentResponseDeliveryService Failing(string failureCode) =>
        new(new AgentResponseDeliveryResult(false, null, failureCode));

    public List<(Guid TenantId, Guid ConversationId, Guid ReservationId, Guid AgentInteractionId, string Content)> Calls { get; } = [];

    public Task<AgentResponseDeliveryResult> SendAsync(
        Guid tenantId, Guid conversationId, Guid reservationId, Guid agentInteractionId, string content, CancellationToken cancellationToken)
    {
        Calls.Add((tenantId, conversationId, reservationId, agentInteractionId, content));
        return Task.FromResult(_result);
    }
}
