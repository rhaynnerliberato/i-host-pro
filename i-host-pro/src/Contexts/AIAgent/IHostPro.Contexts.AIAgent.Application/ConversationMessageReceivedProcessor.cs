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
/// <see cref="ModelProviderException"/> — even after the one controlled
/// retry described below — persists a
/// <see cref="AgentInteractionOutcome.Failure"/> <see cref="AgentInteraction"/>;
/// the session itself is left untouched, but (Checkpoint 5) a deterministic
/// safe fallback message is still delivered to the guest, exactly like any
/// other interaction's response.
///
/// Model call retry (Checkpoint 5, mandate items 26-30): every call to
/// <see cref="IModelProvider.GenerateAsync"/> is retried EXACTLY ONCE
/// (2 attempts total) on a <see cref="ModelProviderException"/> — never more,
/// never for any other kind of failure (unknown tool, business denial,
/// invalid arguments, low confidence are never exceptions in the first
/// place). Call#1 (before any Tool has run): if both attempts fail, no Tool
/// is ever executed and a generic deterministic fallback response is
/// delivered. Call#2 (<see cref="BuildSyntheticResponseAsync"/>, always AFTER
/// a Tool — real or synthetic — has already produced its own sanitized
/// content): if both attempts fail, the orchestrator NEVER re-executes the
/// Tool/Command — it falls back to delivering that already-known, already-safe
/// tool content verbatim as the response, so a real business outcome (e.g. an
/// approved Early Check-in) is never misreported as "could not process your
/// request."
///
/// Unknown Tool (Checkpoint 5, mandate items 24-25): a <c>ToolName</c> the
/// model requests that is not in the fixed <see cref="_tools"/> allowlist is
/// NEVER dispatched via reflection or generic lookup — it is recorded as a
/// failed <see cref="AgentToolExecution"/> (audit only, sanitized failure
/// code) and answered with a safe, generic response, without ever failing
/// the whole interaction (unlike CP3/CP4's original "any tool problem fails
/// the interaction" rule, which still applies to every OTHER kind of tool
/// failure — a business/technical failure from a REAL, known tool).
/// </summary>
public sealed class ConversationMessageReceivedProcessor : IIntegrationEventHandler<ConversationMessageReceived>
{
    private const string AlreadyProcessedReason = "AlreadyProcessed";
    private const string UnknownToolFailureCode = "unknown_tool";
    private const string UnknownToolResponseContent = "No momento não consigo realizar essa ação específica. Posso ajudar com outra coisa?";
    private const string ModelFailureFallbackContent = "Desculpe, não consegui processar sua mensagem agora. Por favor, tente novamente em instantes.";
    private const string NoPendingActionToConfirmContent = "Não há nenhuma ação aguardando confirmação no momento.";
    private const string NoPendingActionToCancelContent = "Não há nenhuma ação aguardando cancelamento no momento.";
    private const string PendingActionCancelledContent = "A ação foi cancelada, conforme solicitado.";
    private const string AnotherPendingActionActiveContent =
        "Já existe uma ação aguardando sua confirmação ou cancelamento. Confirme ou cancele essa ação antes de iniciar outra.";
    private const string ModelFailureFallbackFinishReason = "model_failure_fallback";
    private const string FallbackModelNamePlaceholder = "n/a";

    // Fase 11, Checkpoint 6 (Human Handoff, Safety & Audit).
    private const string UnknownNotificationFailureCode = "unknown";
    private const string HandoffNotifiedAckContent =
        "O atendimento automático foi pausado e sua solicitação foi encaminhada à nossa equipe. Em breve alguém dará continuidade ao seu atendimento.";
    private const string HandoffRequestedOnlyAckContent =
        "O atendimento automático está pausado e sua solicitação de atendimento humano foi registrada.";

    private readonly IAgentSessionResolver _sessionResolver;
    private readonly IAgentSessionRepository _sessionRepository;
    private readonly IAgentInteractionRepository _interactionRepository;
    private readonly IAgentToolExecutionRepository _toolExecutionRepository;
    private readonly IAgentPendingActionRepository _pendingActionRepository;
    private readonly IAgentHumanHandoffRepository _handoffRepository;
    private readonly IAgentHumanHandoffReasonClassifier _handoffReasonClassifier;
    private readonly IAgentToolConfirmationPolicy _confirmationPolicy;
    private readonly IAgentContextBuilder _contextBuilder;
    private readonly IModelProvider _modelProvider;
    private readonly IAgentResponseDeliveryService _responseDeliveryService;
    private readonly IAdministratorNotificationService _administratorNotificationService;
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
        IAgentHumanHandoffRepository handoffRepository,
        IAgentHumanHandoffReasonClassifier handoffReasonClassifier,
        IAgentToolConfirmationPolicy confirmationPolicy,
        IAgentContextBuilder contextBuilder,
        IModelProvider modelProvider,
        IAgentResponseDeliveryService responseDeliveryService,
        IAdministratorNotificationService administratorNotificationService,
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
        _handoffRepository = handoffRepository;
        _handoffReasonClassifier = handoffReasonClassifier;
        _confirmationPolicy = confirmationPolicy;
        _contextBuilder = contextBuilder;
        _modelProvider = modelProvider;
        _responseDeliveryService = responseDeliveryService;
        _administratorNotificationService = administratorNotificationService;
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

        // Fase 11, Checkpoint 6 (mandate item 13/15/17) — the suspended-session
        // guard: a session already Escalated NEVER reaches the model or any
        // Tool, no matter what the new inbound message contains (including a
        // fake Tool/confirmation marker, or a prompt-injection attempt).
        // IAgentSessionResolver reuses this same Escalated session rather
        // than creating a new Active one (see IAgentSessionRepository's own
        // doc comment), so this check reliably catches every subsequent
        // message for the duration of the handoff.
        var session = await _transactionExecutor.ExecuteAsync(
            () => _sessionRepository.GetByIdAsync(sessionId, cancellationToken), cancellationToken);
        if (session!.Status == AgentSessionStatus.Escalated)
        {
            await HandleSuspendedSessionAsync(@event, sessionId, cancellationToken);
            return;
        }

        var baseRequest = await _contextBuilder.BuildAsync(
            @event.TenantId, @event.ConversationId, @event.MessageId, @event.ReservationId, cancellationToken);
        var request = baseRequest with { AvailableTools = _tools.Select(t => t.Descriptor).ToArray() };

        var interactionId = Guid.NewGuid();
        var startedAtUtc = _timeProvider.GetUtcNow();

        try
        {
            var result = await GenerateWithRetryAsync(request, cancellationToken);

            await StartInteractionAsync(@event, sessionId, interactionId, startedAtUtc, result.ModelName, cancellationToken);

            // Fase 11, Checkpoint 6 (mandate item 3/11) — the safety
            // classifier alone maps Intent to a restricted reason; a
            // restricted intent preempts confirmation/tool-call handling
            // entirely (mutually exclusive in practice, but checked first on
            // principle — a real handoff always wins).
            var handoffReasonCode = _handoffReasonClassifier.Classify(result.Intent);
            if (handoffReasonCode is { } reasonCode)
            {
                await ProcessHumanHandoffRequestAsync(@event, sessionId, interactionId, reasonCode, result, cancellationToken);

                _logger.LogInformation(
                    "AIAgent {Trigger} outcome for tenant {TenantId} conversationId {ConversationId} sessionId {SessionId} interactionId {InteractionId}: {Result}",
                    nameof(ConversationMessageReceived), @event.TenantId, @event.ConversationId, sessionId, interactionId, "HumanHandoffRequested");
                return;
            }

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
            await DeliverFallbackResponseAsync(@event, interactionId, ModelFailureFallbackContent, cancellationToken);

            _logger.LogWarning(
                "AIAgent {Trigger} outcome for tenant {TenantId} conversationId {ConversationId} sessionId {SessionId} interactionId {InteractionId}: {Result}",
                nameof(ConversationMessageReceived), @event.TenantId, @event.ConversationId, sessionId, interactionId, "InteractionFailed");
        }
    }

    /// <summary>
    /// Calls <see cref="IModelProvider.GenerateAsync"/> with exactly one
    /// controlled retry on a transient <see cref="ModelProviderException"/>
    /// (Checkpoint 5, mandate item 26) — 2 attempts total, never more; the
    /// second failure propagates to the caller. Checkpoint 7 (mandate item
    /// 44/47): a <see cref="ModelProviderException.IsPermanent"/> failure
    /// (e.g. invalid credentials, a malformed request) skips the retry
    /// entirely and propagates immediately — a retry cannot possibly fix it.
    /// </summary>
    private async Task<ModelResult> GenerateWithRetryAsync(ModelRequest request, CancellationToken cancellationToken)
    {
        try
        {
            return await _modelProvider.GenerateAsync(request, cancellationToken);
        }
        catch (ModelProviderException ex) when (!ex.IsPermanent)
        {
            _logger.LogWarning(
                "AIAgent {Trigger}: model provider call failed, retrying once", nameof(ConversationMessageReceived));
            return await _modelProvider.GenerateAsync(request, cancellationToken);
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
    /// immediately, unchanged from Checkpoint 3), a write Tool: either
    /// requires confirmation (proposes, never executes yet) or executes
    /// immediately (<c>RequestGuestAccessDelivery</c> — the guest's own
    /// explicit request already is the confirmation), or (Checkpoint 5) a
    /// <c>ToolName</c> outside the fixed allowlist entirely — never dispatched,
    /// answered with a safe generic response instead of failing the whole
    /// interaction (mandate items 24/25).
    /// </summary>
    private async Task<ModelResult?> ProcessToolCallRequestAsync(
        ConversationMessageReceived @event, Guid sessionId, Guid interactionId, DateTimeOffset startedAtUtc,
        ModelToolCallRequest toolCallRequest, ModelRequest request, CancellationToken cancellationToken)
    {
        var tool = _tools.FirstOrDefault(t => t.Descriptor.Name == toolCallRequest.ToolName);

        if (tool is null)
        {
            await RecordUnknownToolExecutionAsync(@event, interactionId, toolCallRequest.ToolName, cancellationToken);
            return await BuildSyntheticResponseAsync(request, UnknownToolResponseContent, cancellationToken);
        }

        if (_confirmationPolicy.RequiresConfirmation(toolCallRequest.ToolName))
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
        if (tool is null)
        {
            _logger.LogError(
                "AIAgent {Trigger} pending action references tool {ToolName} that no longer exists for tenant {TenantId} interactionId {InteractionId}",
                nameof(ConversationMessageReceived), pendingAction.ToolName, @event.TenantId, interactionId);
            await FailInteractionAsync(@event, sessionId, interactionId, startedAtUtc, wasAlreadyPersisted: true, cancellationToken);
            return null;
        }

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
    /// Runs exactly one known Tool, always recording a matching
    /// <see cref="AgentToolExecution"/> audit row first — mirrors Checkpoint
    /// 3's own execution shape exactly, generalized to also serve the
    /// post-confirmation execution path. A tool exception is logged for
    /// operator diagnostics only, never persisted;
    /// <see cref="AgentToolExecution.FailureCode"/> stores only the sanitized
    /// exception TYPE name. <paramref name="tool"/> is always a real,
    /// allowlisted Tool here — an unknown <c>ToolName</c> is intercepted
    /// earlier by <see cref="RecordUnknownToolExecutionAsync"/> (Checkpoint 5)
    /// and never reaches this method.
    /// </summary>
    private async Task<(bool Succeeded, string? Content)> ExecuteToolWithAuditAsync(
        ConversationMessageReceived @event, Guid sessionId, Guid interactionId, DateTimeOffset startedAtUtc,
        string toolName, IAgentTool tool, IReadOnlyDictionary<string, string>? arguments, CancellationToken cancellationToken)
    {
        var toolExecutionId = Guid.NewGuid();
        var toolStartedAtUtc = _timeProvider.GetUtcNow();

        AgentToolResult toolResult;
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

    /// <summary>
    /// Records a failed <see cref="AgentToolExecution"/> audit row for a
    /// <c>ToolName</c> the model requested that is not in the fixed
    /// <see cref="_tools"/> allowlist (Checkpoint 5, mandate item 24) —
    /// never dispatched via reflection or a generic lookup; the interaction
    /// itself does NOT fail (see <see cref="ProcessToolCallRequestAsync"/>'s
    /// safe response instead).
    /// </summary>
    private Task RecordUnknownToolExecutionAsync(
        ConversationMessageReceived @event, Guid interactionId, string toolName, CancellationToken cancellationToken) =>
        _transactionExecutor.ExecuteAsync(() =>
        {
            var toolStartedAtUtc = _timeProvider.GetUtcNow();
            var toolExecution = AgentToolExecution.Start(Guid.NewGuid(), @event.TenantId, interactionId, toolName, toolStartedAtUtc);
            toolExecution.CompleteWithFailure(_timeProvider.GetUtcNow(), UnknownToolFailureCode);
            _toolExecutionRepository.Add(toolExecution);
            return Task.FromResult(true);
        }, cancellationToken);

    /// <summary>
    /// Issues Call#2 with <paramref name="toolContent"/> appended as a
    /// <see cref="ModelMessageRole.Tool"/> turn — the same mechanism
    /// Checkpoint 3 already uses for real Tool results, reused here for every
    /// synthetic/orchestration-produced content string too (propose/confirm/
    /// cancel/blocked notices). Retried once (Checkpoint 5, mandate item 26);
    /// if the model still fails after the retry, this method NEVER
    /// propagates — <paramref name="toolContent"/> is already a safe,
    /// human-presentable fact (the Tool/Command's own known outcome), so it
    /// is delivered verbatim instead of a natural-language paraphrase
    /// (mandate item 29/33 — never re-run the Tool, never claim "could not
    /// process" when the underlying action already succeeded).
    /// </summary>
    private async Task<ModelResult> BuildSyntheticResponseAsync(ModelRequest request, string toolContent, CancellationToken cancellationToken)
    {
        var toolMessages = request.Messages.Append(new ModelMessage(ModelMessageRole.Tool, toolContent)).ToArray();
        var followUpRequest = request with { Messages = toolMessages };

        try
        {
            return await GenerateWithRetryAsync(followUpRequest, cancellationToken);
        }
        catch (ModelProviderException)
        {
            _logger.LogWarning(
                "AIAgent {Trigger}: model provider call#2 failed after retry, falling back to the known tool content verbatim",
                nameof(ConversationMessageReceived));

            return new ModelResult(
                Text: toolContent, DetectedLanguage: null, Intent: null, Confidence: null,
                InputTokens: 0, OutputTokens: 0, ModelName: FallbackModelNamePlaceholder, FinishReason: ModelFailureFallbackFinishReason);
        }
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
                finalResult.InputTokens, finalResult.OutputTokens, finalResult.EstimatedCostUsd, finalResult.CostPricingReference);
            _interactionRepository.Update(interaction);

            var session = (await _sessionRepository.GetByIdAsync(sessionId, cancellationToken))!;
            session.RecordInteraction(
                _timeProvider.GetUtcNow(), finalResult.DetectedLanguage, finalResult.Intent, finalResult.Confidence,
                _modelProvider.ProviderName, finalResult.ModelName);
            _sessionRepository.Update(session);

            return true;
        }, cancellationToken);

        await DeliverFallbackResponseAsync(@event, interactionId, finalResult.Text, cancellationToken);
    }

    /// <summary>
    /// Delivers <paramref name="content"/> as a real outbound message and
    /// records <see cref="AgentInteraction.OutboundMessageId"/> on success —
    /// shared by the normal successful-interaction path and (Checkpoint 5)
    /// the exhausted-model-retry fallback path, since both ultimately do the
    /// exact same thing: best-effort delivery of a final answer, regardless
    /// of the interaction's own <see cref="AgentInteractionOutcome"/>
    /// (<see cref="AgentInteraction.RecordOutboundMessage"/> is valid on any
    /// outcome). A delivery failure here is never retried further and never
    /// fails/reopens the interaction — it is logged and the interaction
    /// simply keeps <c>OutboundMessageId = null</c>.
    /// </summary>
    private async Task DeliverFallbackResponseAsync(
        ConversationMessageReceived @event, Guid interactionId, string content, CancellationToken cancellationToken)
    {
        var deliveryResult = await _responseDeliveryService.SendAsync(
            @event.TenantId, @event.ConversationId, @event.ReservationId, interactionId, content, cancellationToken);

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
    /// Fase 11, Checkpoint 6 (mandate item 15/16) — a new inbound message
    /// arriving while <see cref="AgentSessionStatus.Escalated"/> NEVER
    /// reaches <see cref="IModelProvider"/> or any Tool. Communication
    /// already persisted the inbound <c>Message</c> before this handler even
    /// ran (unchanged) — this records a minimal, model-free
    /// <see cref="AgentInteraction"/> and delivers a deterministic auto-ack
    /// reflecting the active handoff's own CURRENT state, never
    /// re-attempting notification (that already happened once, when the
    /// handoff was first requested) and never touching
    /// <see cref="AgentSession"/> at all (it is already Escalated).
    /// </summary>
    private async Task HandleSuspendedSessionAsync(ConversationMessageReceived @event, Guid sessionId, CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        var interactionId = Guid.NewGuid();

        await _transactionExecutor.ExecuteAsync(() =>
        {
            var interaction = AgentInteraction.Start(
                interactionId, @event.TenantId, sessionId, @event.MessageId, _modelProvider.ProviderName, FallbackModelNamePlaceholder, now);
            interaction.CompleteSuccessfully(
                _timeProvider.GetUtcNow(), intent: null, language: null, confidence: null, inputTokens: 0, outputTokens: 0);
            _interactionRepository.Add(interaction);
            return Task.FromResult(true);
        }, cancellationToken);

        var handoff = await _transactionExecutor.ExecuteAsync(
            () => _handoffRepository.GetActiveByAgentSessionIdAsync(sessionId, cancellationToken), cancellationToken);

        var content = handoff?.Status == AgentHumanHandoffStatus.Notified ? HandoffNotifiedAckContent : HandoffRequestedOnlyAckContent;
        await DeliverFallbackResponseAsync(@event, interactionId, content, cancellationToken);

        _logger.LogInformation(
            "AIAgent {Trigger} outcome for tenant {TenantId} conversationId {ConversationId} sessionId {SessionId} interactionId {InteractionId}: {Result}",
            nameof(ConversationMessageReceived), @event.TenantId, @event.ConversationId, sessionId, interactionId, "SuspendedSessionAcknowledged");
    }

    /// <summary>
    /// Fase 11, Checkpoint 6 (mandate item 11) — a restricted intent was just
    /// classified: create the real <see cref="AgentHumanHandoff"/>, escalate
    /// the session, cancel any active <see cref="AgentPendingAction"/>
    /// (mandate item 12 — Cancelled, NEVER executed/rolled back, no business
    /// Command called), attempt the real administrator notification exactly
    /// once, and reply with a deterministic acknowledgement — NEVER
    /// model-generated (mandate item 31), never claiming a notification that
    /// did not actually succeed (mandate item 16/29). Completes the
    /// already-started <paramref name="interactionId"/> directly (bypassing
    /// <see cref="CompleteInteractionAndDeliverResponseAsync"/>'s own call to
    /// <see cref="AgentSession.RecordInteraction"/>, which requires
    /// <see cref="AgentSessionStatus.Active"/> — this session is Escalated by
    /// the time completion happens).
    /// </summary>
    private async Task ProcessHumanHandoffRequestAsync(
        ConversationMessageReceived @event, Guid sessionId, Guid interactionId, AgentHumanHandoffReasonCode reasonCode,
        ModelResult classifyingResult, CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        var handoffId = Guid.NewGuid();

        await _transactionExecutor.ExecuteAsync(async () =>
        {
            var handoff = AgentHumanHandoff.Request(handoffId, @event.TenantId, sessionId, reasonCode, now);
            _handoffRepository.Add(handoff);

            var session = (await _sessionRepository.GetByIdAsync(sessionId, cancellationToken))!;
            session.Escalate(now);
            _sessionRepository.Update(session);

            var pendingAction = await _pendingActionRepository.GetActiveByAgentSessionIdAsync(sessionId, cancellationToken);
            if (pendingAction is not null)
            {
                pendingAction.Cancel(now);
                _pendingActionRepository.Update(pendingAction);
            }

            var interaction = (await _interactionRepository.GetByIdAsync(interactionId, cancellationToken))!;
            interaction.CompleteSuccessfully(
                now, classifyingResult.Intent, classifyingResult.DetectedLanguage, classifyingResult.Confidence,
                classifyingResult.InputTokens, classifyingResult.OutputTokens,
                classifyingResult.EstimatedCostUsd, classifyingResult.CostPricingReference);
            _interactionRepository.Update(interaction);

            return true;
        }, cancellationToken);

        var notificationResult = await _administratorNotificationService.NotifyAsync(
            @event.TenantId, @event.ConversationId, @event.ReservationId, handoffId, reasonCode.ToString(), cancellationToken);

        await _transactionExecutor.ExecuteAsync(async () =>
        {
            var handoff = (await _handoffRepository.GetByIdAsync(handoffId, cancellationToken))!;
            if (notificationResult.IsSuccess)
                handoff.MarkNotified(_timeProvider.GetUtcNow());
            else
                handoff.MarkNotificationFailed(_timeProvider.GetUtcNow(), notificationResult.FailureCode ?? UnknownNotificationFailureCode);
            _handoffRepository.Update(handoff);
            return true;
        }, cancellationToken);

        if (!notificationResult.IsSuccess)
        {
            _logger.LogWarning(
                "AIAgent human handoff notification failed for tenant {TenantId} agentHumanHandoffId {AgentHumanHandoffId}: {FailureCode}",
                @event.TenantId, handoffId, notificationResult.FailureCode);
        }

        var content = notificationResult.IsSuccess ? HandoffNotifiedAckContent : HandoffRequestedOnlyAckContent;
        await DeliverFallbackResponseAsync(@event, interactionId, content, cancellationToken);
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
