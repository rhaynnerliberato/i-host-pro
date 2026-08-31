using IHostPro.BuildingBlocks.Application;
using IHostPro.Contexts.AIAgent.Application.Tools;
using IHostPro.Contexts.AIAgent.Domain;
using IHostPro.Contexts.Communication.Contracts;
using Microsoft.Extensions.Logging;

namespace IHostPro.Contexts.AIAgent.Application;

/// <summary>
/// Reacts to <see cref="ConversationMessageReceived"/> (Fase 11, Checkpoint 2
/// — AI Agent Foundation; extended by Checkpoint 3 — Read Tools &amp; Context
/// Builder), the real session-creation flow: resolve/create the active
/// <see cref="AgentSession"/> → read sanitized conversation history (ADR-030)
/// → build minimal context → call <see cref="IModelProvider"/> → optionally
/// execute exactly one Read Tool and call the model a second time with its
/// sanitized result → persist <see cref="AgentInteraction"/>. NEVER sends
/// anything to the guest — no Communication outbound action, no persisted
/// response text (response delivery remains a future checkpoint's scope).
///
/// Idempotency: looked up by <c>TenantId</c>/<c>InboundMessageId</c> BEFORE
/// resolving a session, calling the model provider, or executing any tool —
/// a redelivered <c>ConversationMessageReceived</c> is a silent, zero-effect
/// no-op, and never repeats either the model call(s) or a tool call.
///
/// Tool-calling loop (Fase 11, Checkpoint 3): when the model's first call
/// requests a tool (<see cref="ModelResult.ToolCallRequest"/>), the
/// <see cref="AgentInteraction"/> row is persisted (InProgress) BEFORE the
/// tool runs, so the child <see cref="AgentToolExecution"/> audit row always
/// has a real parent to reference (this table's own database foreign key).
/// The tool executes at most once per interaction — the model never gets a
/// second chance to request a different/another tool this checkpoint (no
/// multi-hop tool chaining). A tool failure (business failure, unknown tool
/// name, or an unexpected exception) fails the whole interaction exactly
/// like a <see cref="ModelProviderException"/> does — the session is left
/// untouched, no second model call is made.
///
/// Failure (no tool involved): a <see cref="ModelProviderException"/> (Fake
/// provider controlled failure) persists a <see cref="AgentInteractionOutcome.Failure"/>
/// <see cref="AgentInteraction"/> — the session itself is left untouched: no
/// confirmed language/intent/confidence exists to record from a failed call.
/// No outbound Message, no automatic handoff, no retry loop.
/// </summary>
public sealed class ConversationMessageReceivedProcessor : IIntegrationEventHandler<ConversationMessageReceived>
{
    private const string AlreadyProcessedReason = "AlreadyProcessed";
    private const string UnknownToolFailureCode = "unknown_tool";

    private readonly IAgentSessionResolver _sessionResolver;
    private readonly IAgentSessionRepository _sessionRepository;
    private readonly IAgentInteractionRepository _interactionRepository;
    private readonly IAgentToolExecutionRepository _toolExecutionRepository;
    private readonly IAgentContextBuilder _contextBuilder;
    private readonly IModelProvider _modelProvider;
    private readonly IReadOnlyList<IAgentTool> _tools;
    private readonly IAIAgentTransactionExecutor _transactionExecutor;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ConversationMessageReceivedProcessor> _logger;

    public ConversationMessageReceivedProcessor(
        IAgentSessionResolver sessionResolver,
        IAgentSessionRepository sessionRepository,
        IAgentInteractionRepository interactionRepository,
        IAgentToolExecutionRepository toolExecutionRepository,
        IAgentContextBuilder contextBuilder,
        IModelProvider modelProvider,
        IEnumerable<IAgentTool> tools,
        IAIAgentTransactionExecutor transactionExecutor,
        TimeProvider timeProvider,
        ILogger<ConversationMessageReceivedProcessor> logger)
    {
        _sessionResolver = sessionResolver;
        _sessionRepository = sessionRepository;
        _interactionRepository = interactionRepository;
        _toolExecutionRepository = toolExecutionRepository;
        _contextBuilder = contextBuilder;
        _modelProvider = modelProvider;
        _tools = tools.ToArray();
        _transactionExecutor = transactionExecutor;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task HandleAsync(ConversationMessageReceived @event, CancellationToken cancellationToken)
    {
        var existing = await _transactionExecutor.ExecuteAsync(
            () => _interactionRepository.GetByInboundMessageIdAsync(@event.TenantId, @event.MessageId, cancellationToken),
            cancellationToken);
        if (existing is not null)
        {
            _logger.LogInformation(
                "AIAgent {Trigger} skipped for tenant {TenantId}: {Result}",
                nameof(ConversationMessageReceived), @event.TenantId, AlreadyProcessedReason);
            return;
        }

        var sessionId = await _sessionResolver.GetOrCreateActiveSessionIdAsync(
            @event.TenantId, @event.ConversationId, @event.ReservationId, @event.OccurredAtUtc, cancellationToken);

        var baseRequest = await _contextBuilder.BuildAsync(@event.TenantId, @event.ConversationId, cancellationToken);
        var request = baseRequest with { AvailableTools = _tools.Select(t => t.Descriptor).ToArray() };

        var interactionId = Guid.NewGuid();
        var startedAtUtc = _timeProvider.GetUtcNow();

        try
        {
            var result = await _modelProvider.GenerateAsync(request, cancellationToken);

            if (result.ToolCallRequest is { } toolCallRequest)
            {
                var toolOutcome = await ExecuteToolAsync(
                    @event, sessionId, interactionId, startedAtUtc, toolCallRequest, cancellationToken);
                if (!toolOutcome.Succeeded)
                    return;

                var toolMessages = request.Messages.Append(new ModelMessage(ModelMessageRole.Tool, toolOutcome.ToolContent!)).ToArray();
                var followUpRequest = request with { Messages = toolMessages };
                var followUpResult = await _modelProvider.GenerateAsync(followUpRequest, cancellationToken);

                await CompleteInteractionSuccessfullyAsync(sessionId, interactionId, followUpResult, cancellationToken);
            }
            else
            {
                await _transactionExecutor.ExecuteAsync(async () =>
                {
                    var interaction = AgentInteraction.Start(
                        interactionId, @event.TenantId, sessionId, @event.MessageId,
                        _modelProvider.ProviderName, result.ModelName, startedAtUtc);
                    interaction.CompleteSuccessfully(
                        _timeProvider.GetUtcNow(), result.Intent, result.DetectedLanguage, result.Confidence,
                        result.InputTokens, result.OutputTokens);
                    _interactionRepository.Add(interaction);

                    var session = (await _sessionRepository.GetByIdAsync(sessionId, cancellationToken))!;
                    session.RecordInteraction(
                        _timeProvider.GetUtcNow(), result.DetectedLanguage, result.Intent, result.Confidence,
                        _modelProvider.ProviderName, result.ModelName);
                    _sessionRepository.Update(session);

                    return true;
                }, cancellationToken);
            }

            _logger.LogInformation(
                "AIAgent {Trigger} outcome for tenant {TenantId} conversationId {ConversationId} sessionId {SessionId} interactionId {InteractionId}: {Result}",
                nameof(ConversationMessageReceived), @event.TenantId, @event.ConversationId, sessionId, interactionId, "InteractionSucceeded");
        }
        catch (ModelProviderException)
        {
            await FailInteractionAsync(@event, sessionId, interactionId, startedAtUtc, wasAlreadyPersisted: false, cancellationToken);

            _logger.LogWarning(
                "AIAgent {Trigger} outcome for tenant {TenantId} conversationId {ConversationId} sessionId {SessionId} interactionId {InteractionId}: {Result}",
                nameof(ConversationMessageReceived), @event.TenantId, @event.ConversationId, sessionId, interactionId, "InteractionFailed");
        }
    }

    /// <summary>
    /// Persists the <see cref="AgentInteraction"/> (InProgress) BEFORE
    /// running the tool, so <see cref="AgentToolExecution"/>'s own database
    /// foreign key always has a real parent row. Returns the sanitized tool
    /// content on success; on any failure (unknown tool, business failure, or
    /// an unexpected exception) fails the whole interaction itself and
    /// returns a non-succeeded outcome — the caller must return immediately
    /// without a second model call.
    /// </summary>
    private async Task<(bool Succeeded, string? ToolContent)> ExecuteToolAsync(
        ConversationMessageReceived @event, Guid sessionId, Guid interactionId, DateTimeOffset startedAtUtc,
        ModelToolCallRequest toolCallRequest, CancellationToken cancellationToken)
    {
        await _transactionExecutor.ExecuteAsync(() =>
        {
            var interaction = AgentInteraction.Start(
                interactionId, @event.TenantId, sessionId, @event.MessageId,
                _modelProvider.ProviderName, _modelProvider.ModelName, startedAtUtc);
            _interactionRepository.Add(interaction);
            return Task.FromResult(true);
        }, cancellationToken);

        var tool = _tools.FirstOrDefault(t => t.Descriptor.Name == toolCallRequest.ToolName);
        var toolExecutionId = Guid.NewGuid();
        var toolStartedAtUtc = _timeProvider.GetUtcNow();

        AgentToolResult toolResult;
        if (tool is null)
        {
            toolResult = AgentToolResult.Failure(UnknownToolFailureCode);
        }
        else
        {
            var toolContext = new AgentToolContext(@event.TenantId, @event.ConversationId, @event.ReservationId, sessionId, interactionId);
            try
            {
                toolResult = await tool.ExecuteAsync(toolContext, toolCallRequest.Arguments, cancellationToken);
            }
            catch (Exception ex)
            {
                // The exception itself is only ever logged (operator
                // diagnostics) — never persisted; AgentToolExecution.FailureCode
                // stores only the sanitized exception TYPE name.
                _logger.LogError(ex,
                    "AIAgent {Trigger} tool {ToolName} threw for tenant {TenantId} conversationId {ConversationId} interactionId {InteractionId}",
                    nameof(ConversationMessageReceived), toolCallRequest.ToolName, @event.TenantId, @event.ConversationId, interactionId);
                toolResult = AgentToolResult.Failure(ex.GetType().Name);
            }
        }

        await _transactionExecutor.ExecuteAsync(() =>
        {
            var toolExecution = AgentToolExecution.Start(
                toolExecutionId, @event.TenantId, interactionId, toolCallRequest.ToolName, toolStartedAtUtc);
            if (toolResult.IsSuccess)
                toolExecution.CompleteSuccessfully(_timeProvider.GetUtcNow());
            else
                toolExecution.CompleteWithFailure(_timeProvider.GetUtcNow(), toolResult.FailureCode);
            _toolExecutionRepository.Add(toolExecution);
            return Task.FromResult(true);
        }, cancellationToken);

        if (!toolResult.IsSuccess)
        {
            await FailInteractionAsync(@event, sessionId, interactionId, startedAtUtc, wasAlreadyPersisted: true, cancellationToken);

            _logger.LogWarning(
                "AIAgent {Trigger} tool execution failed for tenant {TenantId} conversationId {ConversationId} sessionId {SessionId} interactionId {InteractionId} tool {ToolName}: {FailureCode}",
                nameof(ConversationMessageReceived), @event.TenantId, @event.ConversationId, sessionId, interactionId,
                toolCallRequest.ToolName, toolResult.FailureCode);

            return (false, null);
        }

        return (true, toolResult.Content);
    }

    private async Task CompleteInteractionSuccessfullyAsync(
        Guid sessionId, Guid interactionId, ModelResult result, CancellationToken cancellationToken)
    {
        await _transactionExecutor.ExecuteAsync(async () =>
        {
            var interaction = (await _interactionRepository.GetByIdAsync(interactionId, cancellationToken))!;
            interaction.CompleteSuccessfully(
                _timeProvider.GetUtcNow(), result.Intent, result.DetectedLanguage, result.Confidence,
                result.InputTokens, result.OutputTokens);
            _interactionRepository.Update(interaction);

            var session = (await _sessionRepository.GetByIdAsync(sessionId, cancellationToken))!;
            session.RecordInteraction(
                _timeProvider.GetUtcNow(), result.DetectedLanguage, result.Intent, result.Confidence,
                _modelProvider.ProviderName, result.ModelName);
            _sessionRepository.Update(session);

            return true;
        }, cancellationToken);
    }

    /// <summary>
    /// Marks the interaction as Failed and NEVER touches the session
    /// (mirrors the no-tool <see cref="ModelProviderException"/> path
    /// exactly). <paramref name="wasAlreadyPersisted"/> distinguishes the two
    /// possible states: the tool-call path already inserted the interaction
    /// row (fetch + complete), while the plain first-call failure path never
    /// persisted it at all yet (start + complete in one step).
    /// </summary>
    private async Task FailInteractionAsync(
        ConversationMessageReceived @event, Guid sessionId, Guid interactionId, DateTimeOffset startedAtUtc,
        bool wasAlreadyPersisted, CancellationToken cancellationToken)
    {
        var completedAtUtc = _timeProvider.GetUtcNow();

        await _transactionExecutor.ExecuteAsync(async () =>
        {
            if (wasAlreadyPersisted)
            {
                var interaction = (await _interactionRepository.GetByIdAsync(interactionId, cancellationToken))!;
                interaction.CompleteWithFailure(completedAtUtc);
                _interactionRepository.Update(interaction);
            }
            else
            {
                var interaction = AgentInteraction.Start(
                    interactionId, @event.TenantId, sessionId, @event.MessageId,
                    _modelProvider.ProviderName, _modelProvider.ModelName, startedAtUtc);
                interaction.CompleteWithFailure(completedAtUtc);
                _interactionRepository.Add(interaction);
            }

            return true;
        }, cancellationToken);
    }
}
