using System.Text.Json;
using IHostPro.BuildingBlocks.Application;
using IHostPro.Contexts.AIAgent.Application.Tools;
using IHostPro.Contexts.AIAgent.Domain;
using IHostPro.Contexts.Communication.Contracts;
using Microsoft.Extensions.Logging;

namespace IHostPro.Contexts.AIAgent.Application;

/// <summary>
/// Reacts to <see cref="ConversationMessageReceived"/> (Fase 11, Checkpoint 2
/// — AI Agent Foundation; extended by Checkpoint 3 — Read Tools &amp; Context
/// Builder; extended by Checkpoint 4 — Write Tools &amp; Response Delivery):
/// resolve/create the active <see cref="AgentSession"/> → read sanitized
/// conversation history (ADR-030) → build minimal context → call
/// <see cref="IModelProvider"/> → optionally propose/confirm/cancel/execute
/// exactly one write Tool, or execute exactly one Read Tool → call the model
/// a second time for the final natural-language response → persist
/// <see cref="AgentInteraction"/> → deliver the response as a real outbound
/// message via <see cref="IAgentResponseDeliveryService"/>.
///
/// Idempotency: looked up by <c>TenantId</c>/<c>InboundMessageId</c> BEFORE
/// resolving a session, calling the model provider, executing any tool, or
/// sending any response — a redelivered <c>ConversationMessageReceived</c>
/// is a silent, zero-effect no-op.
///
/// Every real inbound message that reaches the model gets its own
/// <see cref="AgentInteraction"/> row (InProgress) immediately after Call#1
/// returns — regardless of which branch runs next (Checkpoint 4 mandate item
/// 28: a proposal turn and its later confirmation turn are two distinct
/// interactions, never one).
///
/// Write Tool confirmation (Checkpoint 4): the model never decides whether a
/// Tool requires confirmation (<see cref="ModelResult.ToolCallRequest"/>
/// never carries a <c>RequiresConfirmation</c> flag) — this processor alone
/// consults <see cref="IAgentToolConfirmationPolicy"/>, a fixed server-side
/// allowlist, after receiving a tool-call request. A confirmation-required
/// Tool is never executed on first proposal — an <see cref="AgentPendingAction"/>
/// is created instead, and the real Command runs only after a LATER
/// interaction classifies the guest's own reply as
/// <see cref="ModelResult.ConfirmationIntent"/> <see langword="true"/>. At
/// most one active pending action exists per <see cref="AgentSession"/> — a
/// second proposal while one is already active is rejected without creating
/// a second row or executing anything. Cancelling a pending action never
/// calls any business Command — only marks the proposal itself Cancelled.
///
/// Response delivery (Checkpoint 4): every interaction that reaches a final
/// answer — read-only, write-tool, proposal, confirmation, or cancellation —
/// delivers that answer as a real <c>Communication.Message</c> via
/// <see cref="IAgentResponseDeliveryService"/> (Documento 13 §30). A failed
/// delivery never fails the interaction itself and is never retried
/// automatically — <see cref="AgentInteraction.OutboundMessageId"/> simply
/// stays <see langword="null"/>, auditable by its own absence.
///
/// Failure (no tool involved, first model call itself throws): a
/// <see cref="ModelProviderException"/> persists a
/// <see cref="AgentInteractionOutcome.Failure"/> <see cref="AgentInteraction"/>
/// — the session itself is left untouched, no response is ever sent.
/// </summary>
public sealed class ConversationMessageReceivedProcessor : IIntegrationEventHandler<ConversationMessageReceived>
{
    private const string AlreadyProcessedReason = "AlreadyProcessed";
    private const string UnknownToolFailureCode = "unknown_tool";
    private const string NoPendingActionToConfirmContent = "Não há nenhuma ação aguardando confirmação no momento.";
    private const string NoPendingActionToCancelContent = "Não há nenhuma ação aguardando cancelamento no momento.";
    private const string PendingActionCancelledContent = "A ação foi cancelada, conforme solicitado.";
    private const string AnotherPendingActionActiveContent =
        "Já existe uma ação aguardando sua confirmação ou cancelamento. Confirme ou cancele essa ação antes de iniciar outra.";

    private readonly IAgentSessionResolver _sessionResolver;
    private readonly IAgentSessionRepository _sessionRepository;
    private readonly IAgentInteractionRepository _interactionRepository;
    private readonly IAgentToolExecutionRepository _toolExecutionRepository;
    private readonly IAgentPendingActionRepository _pendingActionRepository;
    private readonly IAgentToolConfirmationPolicy _confirmationPolicy;
    private readonly IAgentContextBuilder _contextBuilder;
    private readonly IModelProvider _modelProvider;
    private readonly IAgentResponseDeliveryService _responseDeliveryService;
    private readonly IReadOnlyList<IAgentTool> _tools;
    private readonly IAIAgentTransactionExecutor _transactionExecutor;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ConversationMessageReceivedProcessor> _logger;

    public ConversationMessageReceivedProcessor(
        IAgentSessionResolver sessionResolver,
        IAgentSessionRepository sessionRepository,
        IAgentInteractionRepository interactionRepository,
        IAgentToolExecutionRepository toolExecutionRepository,
        IAgentPendingActionRepository pendingActionRepository,
        IAgentToolConfirmationPolicy confirmationPolicy,
        IAgentContextBuilder contextBuilder,
        IModelProvider modelProvider,
        IAgentResponseDeliveryService responseDeliveryService,
        IEnumerable<IAgentTool> tools,
        IAIAgentTransactionExecutor transactionExecutor,
        TimeProvider timeProvider,
        ILogger<ConversationMessageReceivedProcessor> logger)
    {
        _sessionResolver = sessionResolver;
        _sessionRepository = sessionRepository;
        _interactionRepository = interactionRepository;
        _toolExecutionRepository = toolExecutionRepository;
        _pendingActionRepository = pendingActionRepository;
        _confirmationPolicy = confirmationPolicy;
        _contextBuilder = contextBuilder;
        _modelProvider = modelProvider;
        _responseDeliveryService = responseDeliveryService;
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

        var baseRequest = await _contextBuilder.BuildAsync(@event.TenantId, @event.ConversationId, @event.MessageId, cancellationToken);
        var request = baseRequest with { AvailableTools = _tools.Select(t => t.Descriptor).ToArray() };

        var interactionId = Guid.NewGuid();
        var startedAtUtc = _timeProvider.GetUtcNow();

        try
        {
            var result = await _modelProvider.GenerateAsync(request, cancellationToken);

            await StartInteractionAsync(@event, sessionId, interactionId, startedAtUtc, result.ModelName, cancellationToken);

            var finalResult = result.ConfirmationIntent is { } confirmationIntent
                ? await ProcessConfirmationReplyAsync(@event, sessionId, interactionId, startedAtUtc, confirmationIntent, request, cancellationToken)
                : result.ToolCallRequest is { } toolCallRequest
                    ? await ProcessToolCallRequestAsync(@event, sessionId, interactionId, startedAtUtc, toolCallRequest, request, cancellationToken)
                    : result;

            if (finalResult is null)
            {
                _logger.LogWarning(
                    "AIAgent {Trigger} outcome for tenant {TenantId} conversationId {ConversationId} sessionId {SessionId} interactionId {InteractionId}: {Result}",
                    nameof(ConversationMessageReceived), @event.TenantId, @event.ConversationId, sessionId, interactionId, "InteractionFailed");
                return;
            }

            await CompleteInteractionAndDeliverResponseAsync(@event, sessionId, interactionId, finalResult, cancellationToken);

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

    private Task StartInteractionAsync(
        ConversationMessageReceived @event, Guid sessionId, Guid interactionId, DateTimeOffset startedAtUtc, string modelName, CancellationToken cancellationToken) =>
        _transactionExecutor.ExecuteAsync(() =>
        {
            var interaction = AgentInteraction.Start(
                interactionId, @event.TenantId, sessionId, @event.MessageId, _modelProvider.ProviderName, modelName, startedAtUtc);
            _interactionRepository.Add(interaction);
            return Task.FromResult(true);
        }, cancellationToken);

    /// <summary>
    /// Handles a <c>ToolCallRequest</c> from Call#1 — a Read Tool (executes
    /// immediately, unchanged from Checkpoint 3), or a write Tool: either
    /// requires confirmation (proposes, never executes yet) or executes
    /// immediately (<c>RequestGuestAccessDelivery</c> — the guest's own
    /// explicit request already is the confirmation).
    /// </summary>
    private async Task<ModelResult?> ProcessToolCallRequestAsync(
        ConversationMessageReceived @event, Guid sessionId, Guid interactionId, DateTimeOffset startedAtUtc,
        ModelToolCallRequest toolCallRequest, ModelRequest request, CancellationToken cancellationToken)
    {
        var tool = _tools.FirstOrDefault(t => t.Descriptor.Name == toolCallRequest.ToolName);

        if (tool is not null && _confirmationPolicy.RequiresConfirmation(toolCallRequest.ToolName))
        {
            var existingPending = await _transactionExecutor.ExecuteAsync(
                () => _pendingActionRepository.GetActiveByAgentSessionIdAsync(sessionId, cancellationToken), cancellationToken);
            if (existingPending is not null)
                return await BuildSyntheticResponseAsync(request, AnotherPendingActionActiveContent, cancellationToken);

            if (tool is not IConfirmableAgentTool confirmableTool)
            {
                _logger.LogError(
                    "AIAgent {Trigger} tool {ToolName} is confirmation-required but does not implement IConfirmableAgentTool for tenant {TenantId} interactionId {InteractionId}",
                    nameof(ConversationMessageReceived), toolCallRequest.ToolName, @event.TenantId, interactionId);
                await FailInteractionAsync(@event, sessionId, interactionId, startedAtUtc, wasAlreadyPersisted: true, cancellationToken);
                return null;
            }

            var proposal = confirmableTool.BuildSanitizedArguments(toolCallRequest.Arguments);
            if (!proposal.IsSuccess)
            {
                _logger.LogWarning(
                    "AIAgent {Trigger} tool {ToolName} proposal rejected for tenant {TenantId} interactionId {InteractionId}: {FailureCode}",
                    nameof(ConversationMessageReceived), toolCallRequest.ToolName, @event.TenantId, interactionId, proposal.FailureCode);
                await FailInteractionAsync(@event, sessionId, interactionId, startedAtUtc, wasAlreadyPersisted: true, cancellationToken);
                return null;
            }

            await _transactionExecutor.ExecuteAsync(() =>
            {
                var pendingAction = AgentPendingAction.Propose(
                    Guid.NewGuid(), @event.TenantId, sessionId, interactionId, toolCallRequest.ToolName,
                    proposal.SanitizedArgumentsJson!, _timeProvider.GetUtcNow());
                _pendingActionRepository.Add(pendingAction);
                return Task.FromResult(true);
            }, cancellationToken);

            return await BuildSyntheticResponseAsync(
                request, $"Confirmação necessária para {toolCallRequest.ToolName}. Por favor, confirme para prosseguir.", cancellationToken);
        }

        var (succeeded, content) = await ExecuteToolWithAuditAsync(
            @event, sessionId, interactionId, startedAtUtc, toolCallRequest.ToolName, tool, toolCallRequest.Arguments, cancellationToken);
        return succeeded ? await BuildSyntheticResponseAsync(request, content!, cancellationToken) : null;
    }

    /// <summary>
    /// Handles Call#1 classifying the guest's own message as a reply to a
    /// pending write-tool proposal — <see langword="true"/> confirms and
    /// executes the real Command; <see langword="false"/> cancels the
    /// proposal itself, never any business Command (Checkpoint 4 mandate
    /// item 16). No active pending action is a legitimate conversational
    /// outcome, never a technical failure.
    /// </summary>
    private async Task<ModelResult?> ProcessConfirmationReplyAsync(
        ConversationMessageReceived @event, Guid sessionId, Guid interactionId, DateTimeOffset startedAtUtc,
        bool confirm, ModelRequest request, CancellationToken cancellationToken)
    {
        var pendingAction = await _transactionExecutor.ExecuteAsync(
            () => _pendingActionRepository.GetActiveByAgentSessionIdAsync(sessionId, cancellationToken), cancellationToken);

        if (pendingAction is null || pendingAction.Status != AgentPendingActionStatus.Proposed)
        {
            var noPendingActionContent = confirm ? NoPendingActionToConfirmContent : NoPendingActionToCancelContent;
            return await BuildSyntheticResponseAsync(request, noPendingActionContent, cancellationToken);
        }

        if (!confirm)
        {
            await _transactionExecutor.ExecuteAsync(() =>
            {
                pendingAction.Cancel(_timeProvider.GetUtcNow());
                _pendingActionRepository.Update(pendingAction);
                return Task.FromResult(true);
            }, cancellationToken);

            return await BuildSyntheticResponseAsync(request, PendingActionCancelledContent, cancellationToken);
        }

        await _transactionExecutor.ExecuteAsync(() =>
        {
            pendingAction.Confirm(_timeProvider.GetUtcNow());
            _pendingActionRepository.Update(pendingAction);
            return Task.FromResult(true);
        }, cancellationToken);

        var tool = _tools.FirstOrDefault(t => t.Descriptor.Name == pendingAction.ToolName);
        var arguments = JsonSerializer.Deserialize<Dictionary<string, string>>(pendingAction.SanitizedArguments);

        var (succeeded, content) = await ExecuteToolWithAuditAsync(
            @event, sessionId, interactionId, startedAtUtc, pendingAction.ToolName, tool, arguments, cancellationToken);
        if (!succeeded)
            return null;

        await _transactionExecutor.ExecuteAsync(() =>
        {
            pendingAction.MarkExecuted(_timeProvider.GetUtcNow());
            _pendingActionRepository.Update(pendingAction);
            return Task.FromResult(true);
        }, cancellationToken);

        return await BuildSyntheticResponseAsync(request, content!, cancellationToken);
    }

    /// <summary>
    /// Runs (or synthesizes an "unknown tool" failure for) exactly one Tool,
    /// always recording a matching <see cref="AgentToolExecution"/> audit
    /// row first — mirrors Checkpoint 3's own execution shape exactly,
    /// generalized to also serve the post-confirmation execution path. A
    /// tool exception is logged for operator diagnostics only, never
    /// persisted; <see cref="AgentToolExecution.FailureCode"/> stores only
    /// the sanitized exception TYPE name.
    /// </summary>
    private async Task<(bool Succeeded, string? Content)> ExecuteToolWithAuditAsync(
        ConversationMessageReceived @event, Guid sessionId, Guid interactionId, DateTimeOffset startedAtUtc,
        string toolName, IAgentTool? tool, IReadOnlyDictionary<string, string>? arguments, CancellationToken cancellationToken)
    {
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
                toolResult = await tool.ExecuteAsync(toolContext, arguments, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "AIAgent {Trigger} tool {ToolName} threw for tenant {TenantId} conversationId {ConversationId} interactionId {InteractionId}",
                    nameof(ConversationMessageReceived), toolName, @event.TenantId, @event.ConversationId, interactionId);
                toolResult = AgentToolResult.Failure(ex.GetType().Name);
            }
        }

        await _transactionExecutor.ExecuteAsync(() =>
        {
            var toolExecution = AgentToolExecution.Start(toolExecutionId, @event.TenantId, interactionId, toolName, toolStartedAtUtc);
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
                nameof(ConversationMessageReceived), @event.TenantId, @event.ConversationId, sessionId, interactionId, toolName, toolResult.FailureCode);

            return (false, null);
        }

        return (true, toolResult.Content);
    }

    /// <summary>Issues Call#2 with <paramref name="toolContent"/> appended as a <see cref="ModelMessageRole.Tool"/> turn — the same mechanism Checkpoint 3 already uses for real Tool results, reused here for every synthetic/orchestration-produced content string too (propose/confirm/cancel/blocked notices).</summary>
    private async Task<ModelResult> BuildSyntheticResponseAsync(ModelRequest request, string toolContent, CancellationToken cancellationToken)
    {
        var toolMessages = request.Messages.Append(new ModelMessage(ModelMessageRole.Tool, toolContent)).ToArray();
        var followUpRequest = request with { Messages = toolMessages };
        return await _modelProvider.GenerateAsync(followUpRequest, cancellationToken);
    }

    /// <summary>Persists the interaction's own completion, then delivers the final response as a real outbound message (Checkpoint 4) — a delivery failure never fails the interaction itself (mandate item 30).</summary>
    private async Task CompleteInteractionAndDeliverResponseAsync(
        ConversationMessageReceived @event, Guid sessionId, Guid interactionId, ModelResult finalResult, CancellationToken cancellationToken)
    {
        await _transactionExecutor.ExecuteAsync(async () =>
        {
            var interaction = (await _interactionRepository.GetByIdAsync(interactionId, cancellationToken))!;
            interaction.CompleteSuccessfully(
                _timeProvider.GetUtcNow(), finalResult.Intent, finalResult.DetectedLanguage, finalResult.Confidence,
                finalResult.InputTokens, finalResult.OutputTokens);
            _interactionRepository.Update(interaction);

            var session = (await _sessionRepository.GetByIdAsync(sessionId, cancellationToken))!;
            session.RecordInteraction(
                _timeProvider.GetUtcNow(), finalResult.DetectedLanguage, finalResult.Intent, finalResult.Confidence,
                _modelProvider.ProviderName, finalResult.ModelName);
            _sessionRepository.Update(session);

            return true;
        }, cancellationToken);

        var deliveryResult = await _responseDeliveryService.SendAsync(
            @event.TenantId, @event.ConversationId, @event.ReservationId, interactionId, finalResult.Text, cancellationToken);

        if (deliveryResult.IsSuccess)
        {
            await _transactionExecutor.ExecuteAsync(async () =>
            {
                var interaction = (await _interactionRepository.GetByIdAsync(interactionId, cancellationToken))!;
                interaction.RecordOutboundMessage(deliveryResult.MessageId!.Value);
                _interactionRepository.Update(interaction);
                return true;
            }, cancellationToken);
        }
        else
        {
            _logger.LogWarning(
                "AIAgent response delivery failed for tenant {TenantId} conversationId {ConversationId} interactionId {InteractionId}: {FailureCode}",
                @event.TenantId, @event.ConversationId, interactionId, deliveryResult.FailureCode);
        }
    }

    /// <summary>
    /// Marks the interaction as Failed and NEVER touches the session.
    /// <paramref name="wasAlreadyPersisted"/> is <see langword="false"/>
    /// only for a <see cref="ModelProviderException"/> thrown by Call#1
    /// itself (before <see cref="StartInteractionAsync"/> ever ran) —
    /// every other failure path in this class already started the
    /// interaction first.
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
