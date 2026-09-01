using FluentAssertions;
using IHostPro.Contexts.AIAgent.Application;
using IHostPro.Contexts.AIAgent.Application.Tools;
using IHostPro.Contexts.AIAgent.Domain;
using IHostPro.Contexts.AIAgent.Infrastructure.ModelProviders;
using IHostPro.Contexts.Communication.Contracts;
using Microsoft.Extensions.Logging.Abstractions;

namespace IHostPro.Contexts.AIAgent.Tests.Unit.Application;

/// <summary>
/// Fase 11, Checkpoint 2 (AI Agent Foundation) — mandate item 37: proves the
/// real session-creation flow (<see cref="ConversationMessageReceivedProcessor"/>)
/// at the orchestration level, mirroring <c>InboundGuestMessageProcessorTests</c>'s
/// own precedent (CP1) exactly. Uses the REAL, deterministic <see cref="FakeModelProvider"/>
/// (Infrastructure) rather than a test-only double — it already IS a
/// deterministic test fixture by design (mandate item 16).
///
/// Extended by Checkpoint 3 (tool-calling loop) and Checkpoint 4 (write-tool
/// confirmation loop + response delivery).
/// </summary>
public class ConversationMessageReceivedProcessorTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid ConversationId = Guid.NewGuid();
    private static readonly Guid ReservationId = Guid.NewGuid();
    private static readonly Guid SessionId = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);

    private static ConversationMessageReceived BuildEvent(Guid messageId) => new()
    {
        TenantId = TenantId,
        AggregateId = messageId,
        AggregateType = "Message",
        CorrelationId = Guid.NewGuid(),
        ActorType = "System",
        ConversationId = ConversationId,
        ReservationId = ReservationId,
        MessageId = messageId,
        OccurredAtUtc = Now,
    };

    private static AgentSession NewActiveSession() =>
        AgentSession.Create(SessionId, TenantId, ConversationId, ReservationId, Now);

    private static ConversationMessageReceivedProcessor CreateProcessor(
        FakeAgentInteractionRepository interactionRepository, FakeAgentSessionRepository sessionRepository,
        ModelRequest? request = null, FakeAgentToolExecutionRepository? toolExecutionRepository = null,
        IEnumerable<IAgentTool>? tools = null, FakeAgentPendingActionRepository? pendingActionRepository = null,
        FakeAgentToolConfirmationPolicy? confirmationPolicy = null, FakeAgentResponseDeliveryService? responseDeliveryService = null,
        FakeAgentHumanHandoffRepository? handoffRepository = null, FakeAdministratorNotificationService? administratorNotificationService = null) =>
        new(
            FakeAgentSessionResolver.Returning(SessionId), sessionRepository, interactionRepository,
            toolExecutionRepository ?? new FakeAgentToolExecutionRepository(),
            pendingActionRepository ?? FakeAgentPendingActionRepository.WithExisting(null),
            handoffRepository ?? FakeAgentHumanHandoffRepository.WithExisting(null),
            new AgentHumanHandoffReasonClassifier(),
            confirmationPolicy ?? FakeAgentToolConfirmationPolicy.RequiringNone(),
            FakeAgentContextBuilder.Returning(request ?? new ModelRequest(null, [new ModelMessage(ModelMessageRole.Guest, "Olá")])),
            new FakeModelProvider(NullLogger<FakeModelProvider>.Instance),
            responseDeliveryService ?? FakeAgentResponseDeliveryService.Succeeding(Guid.NewGuid()),
            administratorNotificationService ?? FakeAdministratorNotificationService.Succeeding(),
            tools ?? [],
            new PassThroughAIAgentTransactionExecutor(), TimeProvider.System,
            NullLogger<ConversationMessageReceivedProcessor>.Instance);

    [Fact]
    public async Task HandleAsync_success_persists_an_AgentInteraction_and_updates_session_metadata()
    {
        var messageId = Guid.NewGuid();
        var interactionRepository = FakeAgentInteractionRepository.WithExisting(null);
        var sessionRepository = FakeAgentSessionRepository.WithExisting(NewActiveSession());
        var responseDelivery = FakeAgentResponseDeliveryService.Succeeding(Guid.NewGuid());
        var processor = CreateProcessor(interactionRepository, sessionRepository, responseDeliveryService: responseDelivery);

        await processor.HandleAsync(BuildEvent(messageId), CancellationToken.None);

        interactionRepository.AddedInteractions.Should().ContainSingle();
        var interaction = interactionRepository.AddedInteractions[0];
        interaction.TenantId.Should().Be(TenantId);
        interaction.AgentSessionId.Should().Be(SessionId);
        interaction.InboundMessageId.Should().Be(messageId);
        interaction.Outcome.Should().Be(AgentInteractionOutcome.Success);
        interaction.ModelProvider.Should().Be("Fake");
        interaction.ModelName.Should().Be("fake-model-v1");
        interaction.Language.Should().Be("pt-BR");
        interaction.Intent.Should().BeNull("CP2 defines no intent catalog");
        interaction.Confidence.Should().BeNull("no confidence marker was present in the fixture message");
        interaction.InputTokens.Should().BeGreaterThan(0);
        interaction.OutputTokens.Should().BeGreaterThan(0);
        interaction.OutboundMessageId.Should().NotBeNull("Checkpoint 4 — every successful interaction delivers a real response");

        sessionRepository.UpdatedSessions.Should().ContainSingle();
        sessionRepository.UpdatedSessions[0].Language.Should().Be("pt-BR");

        responseDelivery.Calls.Should().ContainSingle();
        responseDelivery.Calls[0].AgentInteractionId.Should().Be(interaction.Id);
    }

    [Fact]
    public async Task HandleAsync_persists_the_confidence_value_when_the_provider_supplies_one()
    {
        var messageId = Guid.NewGuid();
        var interactionRepository = FakeAgentInteractionRepository.WithExisting(null);
        var sessionRepository = FakeAgentSessionRepository.WithExisting(NewActiveSession());
        var request = new ModelRequest(null, [new ModelMessage(ModelMessageRole.Guest, $"Olá {FakeModelProvider.ConfidenceMarkerPrefix}0.75]")]);
        var processor = CreateProcessor(interactionRepository, sessionRepository, request);

        await processor.HandleAsync(BuildEvent(messageId), CancellationToken.None);

        interactionRepository.AddedInteractions.Should().ContainSingle();
        interactionRepository.AddedInteractions[0].Confidence.Should().Be(0.75m);
    }

    [Fact]
    public async Task HandleAsync_failure_persists_a_Failure_interaction_and_leaves_the_session_untouched()
    {
        var messageId = Guid.NewGuid();
        var interactionRepository = FakeAgentInteractionRepository.WithExisting(null);
        var sessionRepository = FakeAgentSessionRepository.WithExisting(NewActiveSession());
        var responseDelivery = FakeAgentResponseDeliveryService.Succeeding(Guid.NewGuid());
        var request = new ModelRequest(null, [new ModelMessage(ModelMessageRole.Guest, $"oi {FakeModelProvider.FailureTriggerMarker}")]);
        var processor = CreateProcessor(interactionRepository, sessionRepository, request, responseDeliveryService: responseDelivery);

        await processor.HandleAsync(BuildEvent(messageId), CancellationToken.None);

        interactionRepository.AddedInteractions.Should().ContainSingle();
        var interaction = interactionRepository.AddedInteractions[0];
        interaction.Outcome.Should().Be(AgentInteractionOutcome.Failure);
        interaction.InputTokens.Should().Be(0);
        interaction.OutputTokens.Should().Be(0);
        interaction.OutboundMessageId.Should().NotBeNull(
            "Checkpoint 5 — even after the model call fails twice (FailureTriggerMarker always throws, so the one controlled retry does not help), a deterministic safe fallback response is still delivered");

        responseDelivery.Calls.Should().ContainSingle();
        responseDelivery.Calls[0].Content.Should().NotBeNullOrWhiteSpace();

        sessionRepository.UpdatedSessions.Should().BeEmpty(
            "a failed interaction has no confirmed language/intent/confidence to record — the session remains consistent, untouched");
    }

    [Fact]
    public async Task HandleAsync_tool_call_executes_the_tool_once_and_completes_the_interaction_via_a_second_model_call()
    {
        var messageId = Guid.NewGuid();
        var interactionRepository = FakeAgentInteractionRepository.WithExisting(null);
        var sessionRepository = FakeAgentSessionRepository.WithExisting(NewActiveSession());
        var toolExecutionRepository = new FakeAgentToolExecutionRepository();
        var tool = FakeAgentTool.Succeeding("MyTool", "tool result content");
        var request = new ModelRequest(null, [new ModelMessage(ModelMessageRole.Guest, $"Olá {FakeModelProvider.ToolCallTriggerPrefix}MyTool]")]);
        var processor = CreateProcessor(interactionRepository, sessionRepository, request, toolExecutionRepository, [tool]);

        await processor.HandleAsync(BuildEvent(messageId), CancellationToken.None);

        interactionRepository.AddedInteractions.Should().ContainSingle("the interaction is persisted once, before the tool runs, then completed in place");
        var interaction = interactionRepository.AddedInteractions[0];
        interaction.Outcome.Should().Be(AgentInteractionOutcome.Success);

        toolExecutionRepository.AddedExecutions.Should().ContainSingle();
        var toolExecution = toolExecutionRepository.AddedExecutions[0];
        toolExecution.ToolName.Should().Be("MyTool");
        toolExecution.AgentInteractionId.Should().Be(interaction.Id);
        toolExecution.Outcome.Should().Be(AgentToolExecutionOutcome.Success);

        tool.LastContext.Should().NotBeNull();
        tool.LastContext!.TenantId.Should().Be(TenantId);
        tool.LastContext.ReservationId.Should().Be(ReservationId);
        tool.LastContext.AgentSessionId.Should().Be(SessionId);
        tool.LastContext.AgentInteractionId.Should().Be(interaction.Id);

        sessionRepository.UpdatedSessions.Should().ContainSingle("the tool result fed a real second model call, which completed the interaction successfully");
    }

    [Fact]
    public async Task HandleAsync_tool_business_failure_fails_the_interaction_and_leaves_the_session_untouched()
    {
        var messageId = Guid.NewGuid();
        var interactionRepository = FakeAgentInteractionRepository.WithExisting(null);
        var sessionRepository = FakeAgentSessionRepository.WithExisting(NewActiveSession());
        var toolExecutionRepository = new FakeAgentToolExecutionRepository();
        var tool = FakeAgentTool.Failing("MyTool", "some_business_failure");
        var request = new ModelRequest(null, [new ModelMessage(ModelMessageRole.Guest, $"Olá {FakeModelProvider.ToolCallTriggerPrefix}MyTool]")]);
        var processor = CreateProcessor(interactionRepository, sessionRepository, request, toolExecutionRepository, [tool]);

        await processor.HandleAsync(BuildEvent(messageId), CancellationToken.None);

        interactionRepository.AddedInteractions.Should().ContainSingle();
        interactionRepository.AddedInteractions[0].Outcome.Should().Be(AgentInteractionOutcome.Failure);

        toolExecutionRepository.AddedExecutions.Should().ContainSingle();
        toolExecutionRepository.AddedExecutions[0].Outcome.Should().Be(AgentToolExecutionOutcome.Failure);
        toolExecutionRepository.AddedExecutions[0].FailureCode.Should().Be("some_business_failure");

        sessionRepository.UpdatedSessions.Should().BeEmpty("a tool failure fails the whole interaction — no second model call, session left untouched");
    }

    [Fact]
    public async Task HandleAsync_unknown_tool_name_never_dispatches_records_a_sanitized_audit_row_and_still_answers_safely()
    {
        // Fase 11, Checkpoint 5 (mandate items 24/25): a ToolName outside the
        // fixed allowlist is never executed via reflection/generic dispatch —
        // it is audited as a failed AgentToolExecution, but the interaction
        // itself succeeds with a safe, generic response, unlike CP3/CP4's
        // original "any tool problem fails the whole interaction" rule (which
        // still applies to a REAL tool's own business/technical failure).
        var messageId = Guid.NewGuid();
        var interactionRepository = FakeAgentInteractionRepository.WithExisting(null);
        var sessionRepository = FakeAgentSessionRepository.WithExisting(NewActiveSession());
        var toolExecutionRepository = new FakeAgentToolExecutionRepository();
        var responseDelivery = FakeAgentResponseDeliveryService.Succeeding(Guid.NewGuid());
        var request = new ModelRequest(null, [new ModelMessage(ModelMessageRole.Guest, $"Olá {FakeModelProvider.ToolCallTriggerPrefix}NoSuchTool]")]);
        var processor = CreateProcessor(
            interactionRepository, sessionRepository, request, toolExecutionRepository, [], responseDeliveryService: responseDelivery);

        await processor.HandleAsync(BuildEvent(messageId), CancellationToken.None);

        interactionRepository.AddedInteractions[0].Outcome.Should().Be(AgentInteractionOutcome.Success);
        interactionRepository.AddedInteractions[0].OutboundMessageId.Should().NotBeNull();
        toolExecutionRepository.AddedExecutions.Should().ContainSingle("the unknown tool name is still audited, even though it never dispatches");
        toolExecutionRepository.AddedExecutions[0].Outcome.Should().Be(AgentToolExecutionOutcome.Failure);
        toolExecutionRepository.AddedExecutions[0].FailureCode.Should().Be("unknown_tool");
        sessionRepository.UpdatedSessions.Should().ContainSingle("the interaction completed successfully with a safe response");
        responseDelivery.Calls.Should().ContainSingle();
    }

    [Fact]
    public async Task HandleAsync_tool_throwing_fails_the_interaction_with_the_exception_type_name_as_FailureCode()
    {
        var messageId = Guid.NewGuid();
        var interactionRepository = FakeAgentInteractionRepository.WithExisting(null);
        var sessionRepository = FakeAgentSessionRepository.WithExisting(NewActiveSession());
        var toolExecutionRepository = new FakeAgentToolExecutionRepository();
        var tool = FakeAgentTool.Throwing("MyTool", new InvalidOperationException("boom"));
        var request = new ModelRequest(null, [new ModelMessage(ModelMessageRole.Guest, $"Olá {FakeModelProvider.ToolCallTriggerPrefix}MyTool]")]);
        var processor = CreateProcessor(interactionRepository, sessionRepository, request, toolExecutionRepository, [tool]);

        await processor.HandleAsync(BuildEvent(messageId), CancellationToken.None);

        interactionRepository.AddedInteractions[0].Outcome.Should().Be(AgentInteractionOutcome.Failure);
        toolExecutionRepository.AddedExecutions[0].FailureCode.Should().Be(nameof(InvalidOperationException), "the raw exception message is never persisted, only the sanitized type name");
        sessionRepository.UpdatedSessions.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleAsync_skips_when_the_same_InboundMessageId_was_already_processed()
    {
        var messageId = Guid.NewGuid();
        var existing = AgentInteraction.Start(Guid.NewGuid(), TenantId, SessionId, messageId, "Fake", "fake-model-v1", Now);
        var interactionRepository = FakeAgentInteractionRepository.WithExisting(existing);
        var sessionRepository = FakeAgentSessionRepository.WithExisting(NewActiveSession());
        var processor = CreateProcessor(interactionRepository, sessionRepository);

        await processor.HandleAsync(BuildEvent(messageId), CancellationToken.None);

        interactionRepository.AddedInteractions.Should().BeEmpty("a redelivered ConversationMessageReceived must never create a second AgentInteraction");
        sessionRepository.UpdatedSessions.Should().BeEmpty("the idempotency short-circuit happens before any session/model work");
    }

    [Fact]
    public async Task HandleAsync_response_delivery_failure_leaves_OutboundMessageId_null_but_does_not_fail_the_interaction()
    {
        var messageId = Guid.NewGuid();
        var interactionRepository = FakeAgentInteractionRepository.WithExisting(null);
        var sessionRepository = FakeAgentSessionRepository.WithExisting(NewActiveSession());
        var responseDelivery = FakeAgentResponseDeliveryService.Failing("connector_exception");
        var processor = CreateProcessor(interactionRepository, sessionRepository, responseDeliveryService: responseDelivery);

        await processor.HandleAsync(BuildEvent(messageId), CancellationToken.None);

        var interaction = interactionRepository.AddedInteractions[0];
        interaction.Outcome.Should().Be(AgentInteractionOutcome.Success, "a response delivery failure never fails the interaction itself (CP4 mandate item 30)");
        interaction.OutboundMessageId.Should().BeNull("never marked as sent artificially");
        sessionRepository.UpdatedSessions.Should().ContainSingle("session metadata is still recorded even if delivery fails");
    }

    // ---- Checkpoint 4: write-tool confirmation loop ------------------------

    [Fact]
    public async Task HandleAsync_confirmation_required_tool_creates_a_pending_action_and_never_executes_the_command()
    {
        var messageId = Guid.NewGuid();
        var interactionRepository = FakeAgentInteractionRepository.WithExisting(null);
        var sessionRepository = FakeAgentSessionRepository.WithExisting(NewActiveSession());
        var pendingActionRepository = FakeAgentPendingActionRepository.WithExisting(null);
        var confirmationPolicy = FakeAgentToolConfirmationPolicy.RequiringConfirmationFor("RequestEarlyCheckIn");
        var tool = FakeConfirmableAgentTool.Succeeding("RequestEarlyCheckIn", """{"requestedCheckInAt":"2026-09-01T12:00:00Z"}""", "should never execute");
        var request = new ModelRequest(
            null, [new ModelMessage(ModelMessageRole.Guest, $"quero early check-in {FakeModelProvider.ToolCallTriggerPrefix}RequestEarlyCheckIn:requestedCheckInAt=2026-09-01T12:00:00Z]")]);
        var processor = CreateProcessor(
            interactionRepository, sessionRepository, request, tools: [tool],
            pendingActionRepository: pendingActionRepository, confirmationPolicy: confirmationPolicy);

        await processor.HandleAsync(BuildEvent(messageId), CancellationToken.None);

        pendingActionRepository.AddedPendingActions.Should().ContainSingle();
        var pendingAction = pendingActionRepository.AddedPendingActions[0];
        pendingAction.ToolName.Should().Be("RequestEarlyCheckIn");
        pendingAction.Status.Should().Be(AgentPendingActionStatus.Proposed);
        pendingAction.AgentSessionId.Should().Be(SessionId);

        tool.ExecuteCallCount.Should().Be(0, "the real business Command must never run on first proposal");
        interactionRepository.AddedInteractions[0].Outcome.Should().Be(AgentInteractionOutcome.Success, "proposing is itself a successful interaction");
        sessionRepository.UpdatedSessions.Should().ContainSingle();
    }

    [Fact]
    public async Task HandleAsync_confirmation_intent_executes_the_pending_action_and_marks_it_Executed()
    {
        var sessionRepository = FakeAgentSessionRepository.WithExisting(NewActiveSession());
        var interactionRepository = FakeAgentInteractionRepository.WithExisting(null);
        var toolExecutionRepository = new FakeAgentToolExecutionRepository();
        var pendingActionRepository = FakeAgentPendingActionRepository.WithExisting(null);
        var confirmationPolicy = FakeAgentToolConfirmationPolicy.RequiringConfirmationFor("RequestEarlyCheckIn");
        var tool = FakeConfirmableAgentTool.Succeeding("RequestEarlyCheckIn", """{"requestedCheckInAt":"2026-09-01T12:00:00Z"}""", "Pedido de early check-in: approved.");

        var proposeMessageId = Guid.NewGuid();
        var proposeRequest = new ModelRequest(
            null, [new ModelMessage(ModelMessageRole.Guest, $"quero early check-in {FakeModelProvider.ToolCallTriggerPrefix}RequestEarlyCheckIn:requestedCheckInAt=2026-09-01T12:00:00Z]")]);
        var proposeProcessor = CreateProcessor(
            interactionRepository, sessionRepository, proposeRequest, toolExecutionRepository, [tool],
            pendingActionRepository, confirmationPolicy);
        await proposeProcessor.HandleAsync(BuildEvent(proposeMessageId), CancellationToken.None);

        var confirmMessageId = Guid.NewGuid();
        var confirmRequest = new ModelRequest(null, [new ModelMessage(ModelMessageRole.Guest, $"sim, confirmo {FakeModelProvider.ConfirmTriggerMarker}")]);
        var confirmProcessor = CreateProcessor(
            interactionRepository, sessionRepository, confirmRequest, toolExecutionRepository, [tool],
            pendingActionRepository, confirmationPolicy);
        await confirmProcessor.HandleAsync(BuildEvent(confirmMessageId), CancellationToken.None);

        interactionRepository.AddedInteractions.Should().HaveCount(2, "the proposal and the confirmation are two distinct interactions");

        var pendingAction = pendingActionRepository.AddedPendingActions[0];
        pendingAction.Status.Should().Be(AgentPendingActionStatus.Executed);
        pendingAction.ConfirmedAtUtc.Should().NotBeNull();
        pendingAction.ExecutedAtUtc.Should().NotBeNull();

        tool.ExecuteCallCount.Should().Be(1, "the real Command executes exactly once, only after confirmation");
        tool.LastExecuteArguments.Should().ContainKey("requestedCheckInAt").WhoseValue.Should().Be("2026-09-01T12:00:00Z");

        toolExecutionRepository.AddedExecutions.Should().ContainSingle("only the confirmed execution is audited as a real tool run — the proposal itself never was");
        interactionRepository.AddedInteractions[1].Outcome.Should().Be(AgentInteractionOutcome.Success);
    }

    [Fact]
    public async Task HandleAsync_cancellation_intent_cancels_the_pending_action_without_calling_any_command()
    {
        var sessionRepository = FakeAgentSessionRepository.WithExisting(NewActiveSession());
        var interactionRepository = FakeAgentInteractionRepository.WithExisting(null);
        var pendingActionRepository = FakeAgentPendingActionRepository.WithExisting(null);
        var confirmationPolicy = FakeAgentToolConfirmationPolicy.RequiringConfirmationFor("RequestEarlyCheckIn");
        var tool = FakeConfirmableAgentTool.Succeeding("RequestEarlyCheckIn", """{"requestedCheckInAt":"2026-09-01T12:00:00Z"}""", "should never execute");

        var proposeRequest = new ModelRequest(
            null, [new ModelMessage(ModelMessageRole.Guest, $"quero early check-in {FakeModelProvider.ToolCallTriggerPrefix}RequestEarlyCheckIn:requestedCheckInAt=2026-09-01T12:00:00Z]")]);
        await CreateProcessor(interactionRepository, sessionRepository, proposeRequest, tools: [tool], pendingActionRepository: pendingActionRepository, confirmationPolicy: confirmationPolicy)
            .HandleAsync(BuildEvent(Guid.NewGuid()), CancellationToken.None);

        var cancelRequest = new ModelRequest(null, [new ModelMessage(ModelMessageRole.Guest, $"deixa pra lá {FakeModelProvider.CancelTriggerMarker}")]);
        await CreateProcessor(interactionRepository, sessionRepository, cancelRequest, tools: [tool], pendingActionRepository: pendingActionRepository, confirmationPolicy: confirmationPolicy)
            .HandleAsync(BuildEvent(Guid.NewGuid()), CancellationToken.None);

        pendingActionRepository.AddedPendingActions[0].Status.Should().Be(AgentPendingActionStatus.Cancelled);
        tool.ExecuteCallCount.Should().Be(0, "cancelling never calls the business Command");
    }

    [Fact]
    public async Task HandleAsync_a_second_proposal_while_one_is_already_pending_is_rejected_without_creating_a_second_row()
    {
        var sessionRepository = FakeAgentSessionRepository.WithExisting(NewActiveSession());
        var interactionRepository = FakeAgentInteractionRepository.WithExisting(null);
        var pendingActionRepository = FakeAgentPendingActionRepository.WithExisting(null);
        var confirmationPolicy = FakeAgentToolConfirmationPolicy.RequiringConfirmationFor("RequestEarlyCheckIn");
        var tool = FakeConfirmableAgentTool.Succeeding("RequestEarlyCheckIn", """{"requestedCheckInAt":"2026-09-01T12:00:00Z"}""", "should never execute");
        var proposeRequest = new ModelRequest(
            null, [new ModelMessage(ModelMessageRole.Guest, $"quero early check-in {FakeModelProvider.ToolCallTriggerPrefix}RequestEarlyCheckIn:requestedCheckInAt=2026-09-01T12:00:00Z]")]);

        await CreateProcessor(interactionRepository, sessionRepository, proposeRequest, tools: [tool], pendingActionRepository: pendingActionRepository, confirmationPolicy: confirmationPolicy)
            .HandleAsync(BuildEvent(Guid.NewGuid()), CancellationToken.None);
        await CreateProcessor(interactionRepository, sessionRepository, proposeRequest, tools: [tool], pendingActionRepository: pendingActionRepository, confirmationPolicy: confirmationPolicy)
            .HandleAsync(BuildEvent(Guid.NewGuid()), CancellationToken.None);

        pendingActionRepository.AddedPendingActions.Should().ContainSingle("a second proposal must never create a second active pending action");
        interactionRepository.AddedInteractions.Should().HaveCount(2);
        interactionRepository.AddedInteractions[1].Outcome.Should().Be(AgentInteractionOutcome.Success, "the block itself is a successful, explained interaction");
    }

    [Fact]
    public async Task HandleAsync_confirmation_intent_with_no_pending_action_succeeds_with_a_generic_response_and_calls_no_tool()
    {
        var sessionRepository = FakeAgentSessionRepository.WithExisting(NewActiveSession());
        var interactionRepository = FakeAgentInteractionRepository.WithExisting(null);
        var confirmRequest = new ModelRequest(null, [new ModelMessage(ModelMessageRole.Guest, $"sim {FakeModelProvider.ConfirmTriggerMarker}")]);
        var processor = CreateProcessor(interactionRepository, sessionRepository, confirmRequest);

        await processor.HandleAsync(BuildEvent(Guid.NewGuid()), CancellationToken.None);

        interactionRepository.AddedInteractions[0].Outcome.Should().Be(AgentInteractionOutcome.Success);
        sessionRepository.UpdatedSessions.Should().ContainSingle();
    }

    [Fact]
    public async Task HandleAsync_confirmation_required_tool_proposal_rejected_by_the_tool_fails_the_interaction()
    {
        var sessionRepository = FakeAgentSessionRepository.WithExisting(NewActiveSession());
        var interactionRepository = FakeAgentInteractionRepository.WithExisting(null);
        var pendingActionRepository = FakeAgentPendingActionRepository.WithExisting(null);
        var confirmationPolicy = FakeAgentToolConfirmationPolicy.RequiringConfirmationFor("RequestEarlyCheckIn");
        var tool = FakeConfirmableAgentTool.RejectingProposal("RequestEarlyCheckIn", "invalid_requested_check_in_at");
        var request = new ModelRequest(null, [new ModelMessage(ModelMessageRole.Guest, $"Olá {FakeModelProvider.ToolCallTriggerPrefix}RequestEarlyCheckIn]")]);
        var processor = CreateProcessor(
            interactionRepository, sessionRepository, request, tools: [tool],
            pendingActionRepository: pendingActionRepository, confirmationPolicy: confirmationPolicy);

        await processor.HandleAsync(BuildEvent(Guid.NewGuid()), CancellationToken.None);

        interactionRepository.AddedInteractions[0].Outcome.Should().Be(AgentInteractionOutcome.Failure);
        pendingActionRepository.AddedPendingActions.Should().BeEmpty("invalid arguments never create a pending action");
        sessionRepository.UpdatedSessions.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleAsync_confirmed_execution_technical_failure_fails_the_interaction_and_leaves_the_pending_action_Confirmed()
    {
        var sessionRepository = FakeAgentSessionRepository.WithExisting(NewActiveSession());
        var interactionRepository = FakeAgentInteractionRepository.WithExisting(null);
        var toolExecutionRepository = new FakeAgentToolExecutionRepository();
        var pendingActionRepository = FakeAgentPendingActionRepository.WithExisting(null);
        var confirmationPolicy = FakeAgentToolConfirmationPolicy.RequiringConfirmationFor("RequestEarlyCheckIn");
        var tool = FakeConfirmableAgentTool.FailingExecution("RequestEarlyCheckIn", """{"requestedCheckInAt":"2026-09-01T12:00:00Z"}""", "ReservationNotFound");

        var proposeRequest = new ModelRequest(
            null, [new ModelMessage(ModelMessageRole.Guest, $"quero early check-in {FakeModelProvider.ToolCallTriggerPrefix}RequestEarlyCheckIn:requestedCheckInAt=2026-09-01T12:00:00Z]")]);
        await CreateProcessor(interactionRepository, sessionRepository, proposeRequest, toolExecutionRepository, [tool], pendingActionRepository, confirmationPolicy)
            .HandleAsync(BuildEvent(Guid.NewGuid()), CancellationToken.None);

        var confirmRequest = new ModelRequest(null, [new ModelMessage(ModelMessageRole.Guest, $"sim {FakeModelProvider.ConfirmTriggerMarker}")]);
        await CreateProcessor(interactionRepository, sessionRepository, confirmRequest, toolExecutionRepository, [tool], pendingActionRepository, confirmationPolicy)
            .HandleAsync(BuildEvent(Guid.NewGuid()), CancellationToken.None);

        interactionRepository.AddedInteractions[1].Outcome.Should().Be(AgentInteractionOutcome.Failure);
        pendingActionRepository.AddedPendingActions[0].Status.Should().Be(
            AgentPendingActionStatus.Confirmed, "a technical execution failure never marks the pending action Executed");
        toolExecutionRepository.AddedExecutions.Should().ContainSingle();
        toolExecutionRepository.AddedExecutions[0].FailureCode.Should().Be("ReservationNotFound");
    }

    [Fact]
    public async Task HandleAsync_a_tool_with_no_confirmation_policy_entry_executes_immediately_with_no_pending_action()
    {
        var messageId = Guid.NewGuid();
        var interactionRepository = FakeAgentInteractionRepository.WithExisting(null);
        var sessionRepository = FakeAgentSessionRepository.WithExisting(NewActiveSession());
        var pendingActionRepository = FakeAgentPendingActionRepository.WithExisting(null);
        var tool = FakeAgentTool.Succeeding("RequestGuestAccessDelivery", "Solicitação de envio de acesso registrada com sucesso.");
        var request = new ModelRequest(null, [new ModelMessage(ModelMessageRole.Guest, $"me envie a senha {FakeModelProvider.ToolCallTriggerPrefix}RequestGuestAccessDelivery]")]);
        var processor = CreateProcessor(
            interactionRepository, sessionRepository, request, tools: [tool], pendingActionRepository: pendingActionRepository,
            confirmationPolicy: FakeAgentToolConfirmationPolicy.RequiringNone());

        await processor.HandleAsync(BuildEvent(messageId), CancellationToken.None);

        pendingActionRepository.AddedPendingActions.Should().BeEmpty("EXPLICIT_REQUEST_IS_CONFIRMATION tools never create a pending action");
        interactionRepository.AddedInteractions[0].Outcome.Should().Be(AgentInteractionOutcome.Success);
        sessionRepository.UpdatedSessions.Should().ContainSingle();
    }

    // Fase 11, Checkpoint 5 — Policies, Workflow & Conversational Orchestration.

    [Fact]
    public async Task HandleAsync_a_transient_Call1_model_failure_is_retried_once_and_the_interaction_succeeds()
    {
        var messageId = Guid.NewGuid();
        var interactionRepository = FakeAgentInteractionRepository.WithExisting(null);
        var sessionRepository = FakeAgentSessionRepository.WithExisting(NewActiveSession());
        var request = new ModelRequest(null, [new ModelMessage(ModelMessageRole.Guest, $"oi {FakeModelProvider.TransientFailureTriggerMarker}")]);
        var processor = CreateProcessor(interactionRepository, sessionRepository, request);

        await processor.HandleAsync(BuildEvent(messageId), CancellationToken.None);

        interactionRepository.AddedInteractions.Should().ContainSingle();
        var interaction = interactionRepository.AddedInteractions[0];
        interaction.Outcome.Should().Be(AgentInteractionOutcome.Success, "attempt #1 fails but the one controlled retry succeeds");
        interaction.OutboundMessageId.Should().NotBeNull();
        sessionRepository.UpdatedSessions.Should().ContainSingle();
    }

    [Fact]
    public async Task HandleAsync_a_permanent_Call1_model_failure_survives_the_retry_and_still_fails_with_no_tool_executed()
    {
        var messageId = Guid.NewGuid();
        var interactionRepository = FakeAgentInteractionRepository.WithExisting(null);
        var sessionRepository = FakeAgentSessionRepository.WithExisting(NewActiveSession());
        var toolExecutionRepository = new FakeAgentToolExecutionRepository();
        var tool = FakeAgentTool.Succeeding("MyTool", "should never run");
        var request = new ModelRequest(null, [new ModelMessage(ModelMessageRole.Guest, $"oi {FakeModelProvider.FailureTriggerMarker}")]);
        var processor = CreateProcessor(interactionRepository, sessionRepository, request, toolExecutionRepository, [tool]);

        await processor.HandleAsync(BuildEvent(messageId), CancellationToken.None);

        interactionRepository.AddedInteractions[0].Outcome.Should().Be(AgentInteractionOutcome.Failure);
        toolExecutionRepository.AddedExecutions.Should().BeEmpty("Call#1 never even reaches a ToolCallRequest when it throws");
        tool.LastContext.Should().BeNull();
    }

    [Fact]
    public async Task HandleAsync_a_permanent_Call2_model_failure_falls_back_to_the_known_tool_content_verbatim_and_the_interaction_succeeds()
    {
        // Fase 11, Checkpoint 5 (mandate item 29/33): the write Tool/Command
        // already succeeded — Call#2 (turning that known outcome into
        // natural language) fails both attempts. The orchestrator must NEVER
        // re-run the tool, and must never claim "could not process your
        // request" when the underlying action already succeeded — it falls
        // back to the tool's own already-safe content, verbatim.
        var messageId = Guid.NewGuid();
        var interactionRepository = FakeAgentInteractionRepository.WithExisting(null);
        var sessionRepository = FakeAgentSessionRepository.WithExisting(NewActiveSession());
        var toolExecutionRepository = new FakeAgentToolExecutionRepository();
        const string knownToolContent = "Pedido de early check-in: approved.";
        var tool = FakeAgentTool.Succeeding("MyTool", $"{knownToolContent} {FakeModelProvider.FailureTriggerMarker}");
        var request = new ModelRequest(null, [new ModelMessage(ModelMessageRole.Guest, $"Olá {FakeModelProvider.ToolCallTriggerPrefix}MyTool]")]);
        var processor = CreateProcessor(interactionRepository, sessionRepository, request, toolExecutionRepository, [tool]);

        await processor.HandleAsync(BuildEvent(messageId), CancellationToken.None);

        toolExecutionRepository.AddedExecutions.Should().ContainSingle();
        toolExecutionRepository.AddedExecutions[0].Outcome.Should().Be(AgentToolExecutionOutcome.Success, "the Tool/Command itself genuinely succeeded");

        interactionRepository.AddedInteractions.Should().ContainSingle();
        var interaction = interactionRepository.AddedInteractions[0];
        interaction.Outcome.Should().Be(AgentInteractionOutcome.Success, "a Call#2 model failure never invalidates an already-successful Tool/Command");
        interaction.OutboundMessageId.Should().NotBeNull();
        sessionRepository.UpdatedSessions.Should().ContainSingle();
    }

    [Fact]
    public async Task HandleAsync_unsupported_request_intent_produces_a_safe_response_and_calls_no_tool()
    {
        var messageId = Guid.NewGuid();
        var interactionRepository = FakeAgentInteractionRepository.WithExisting(null);
        var sessionRepository = FakeAgentSessionRepository.WithExisting(NewActiveSession());
        var toolExecutionRepository = new FakeAgentToolExecutionRepository();
        var request = new ModelRequest(null, [new ModelMessage(ModelMessageRole.Guest, $"quero cancelar minha reserva {FakeModelProvider.UnsupportedRequestTriggerMarker}")]);
        var processor = CreateProcessor(interactionRepository, sessionRepository, request, toolExecutionRepository, []);

        await processor.HandleAsync(BuildEvent(messageId), CancellationToken.None);

        var interaction = interactionRepository.AddedInteractions[0];
        interaction.Outcome.Should().Be(AgentInteractionOutcome.Success);
        interaction.Intent.Should().Be("unsupported_request");
        interaction.OutboundMessageId.Should().NotBeNull();
        toolExecutionRepository.AddedExecutions.Should().BeEmpty("no Tool/Command is ever called for an unsupported request");
    }

    [Fact]
    public async Task HandleAsync_human_handoff_requested_intent_produces_a_safe_response_no_business_tool_and_zero_state_mutation_beyond_audit()
    {
        // Fase 11, Checkpoint 5: this intent was classified but no handoff
        // action existed yet. Fase 11, Checkpoint 6: the SAME intent now
        // triggers the real handoff — this test's own name/assertions were
        // updated to match, an intentional behavior change (mirrors every
        // other CP-to-CP assertion update already made in this class).
        var messageId = Guid.NewGuid();
        var interactionRepository = FakeAgentInteractionRepository.WithExisting(null);
        var sessionRepository = FakeAgentSessionRepository.WithExisting(NewActiveSession());
        var toolExecutionRepository = new FakeAgentToolExecutionRepository();
        var request = new ModelRequest(null, [new ModelMessage(ModelMessageRole.Guest, $"quero falar com uma pessoa {FakeModelProvider.HumanHandoffTriggerMarker}")]);
        var processor = CreateProcessor(interactionRepository, sessionRepository, request, toolExecutionRepository, []);

        await processor.HandleAsync(BuildEvent(messageId), CancellationToken.None);

        var interaction = interactionRepository.AddedInteractions[0];
        interaction.Outcome.Should().Be(AgentInteractionOutcome.Success);
        interaction.Intent.Should().Be("human_handoff_requested");
        interaction.OutboundMessageId.Should().NotBeNull();
        toolExecutionRepository.AddedExecutions.Should().BeEmpty("a human handoff never calls any business Tool/Command");

        sessionRepository.UpdatedSessions.Should().ContainSingle();
        sessionRepository.UpdatedSessions[0].Status.Should().Be(AgentSessionStatus.Escalated);
    }

    // Fase 11, Checkpoint 6 — Human Handoff, Safety & Audit.

    [Fact]
    public async Task HandleAsync_a_restricted_intent_creates_a_real_handoff_escalates_the_session_and_notifies_the_administrator()
    {
        var messageId = Guid.NewGuid();
        var interactionRepository = FakeAgentInteractionRepository.WithExisting(null);
        var sessionRepository = FakeAgentSessionRepository.WithExisting(NewActiveSession());
        var handoffRepository = FakeAgentHumanHandoffRepository.WithExisting(null);
        var notificationService = FakeAdministratorNotificationService.Succeeding();
        var request = new ModelRequest(null, [new ModelMessage(ModelMessageRole.Guest, $"quero um reembolso {FakeModelProvider.IntentTriggerPrefix}refund]")]);
        var processor = CreateProcessor(
            interactionRepository, sessionRepository, request,
            handoffRepository: handoffRepository, administratorNotificationService: notificationService);

        await processor.HandleAsync(BuildEvent(messageId), CancellationToken.None);

        handoffRepository.AddedHandoffs.Should().ContainSingle();
        var handoff = handoffRepository.AddedHandoffs[0];
        handoff.ReasonCode.Should().Be(AgentHumanHandoffReasonCode.Refund);
        handoff.Status.Should().Be(AgentHumanHandoffStatus.Notified, "the notification service succeeded");
        handoff.NotifiedAtUtc.Should().NotBeNull();

        sessionRepository.UpdatedSessions.Should().ContainSingle();
        sessionRepository.UpdatedSessions[0].Status.Should().Be(AgentSessionStatus.Escalated);

        notificationService.Calls.Should().ContainSingle();
        notificationService.Calls[0].ReasonCode.Should().Be("Refund");

        var interaction = interactionRepository.AddedInteractions[0];
        interaction.Outcome.Should().Be(AgentInteractionOutcome.Success);
        interaction.OutboundMessageId.Should().NotBeNull();
    }

    [Fact]
    public async Task HandleAsync_a_failed_administrator_notification_never_reactivates_the_session_and_never_claims_success()
    {
        var messageId = Guid.NewGuid();
        var interactionRepository = FakeAgentInteractionRepository.WithExisting(null);
        var sessionRepository = FakeAgentSessionRepository.WithExisting(NewActiveSession());
        var handoffRepository = FakeAgentHumanHandoffRepository.WithExisting(null);
        var notificationService = FakeAdministratorNotificationService.Failing("connector_exception");
        var responseDelivery = FakeAgentResponseDeliveryService.Succeeding(Guid.NewGuid());
        var request = new ModelRequest(null, [new ModelMessage(ModelMessageRole.Guest, $"acidente grave {FakeModelProvider.IntentTriggerPrefix}accident]")]);
        var processor = CreateProcessor(
            interactionRepository, sessionRepository, request,
            handoffRepository: handoffRepository, administratorNotificationService: notificationService, responseDeliveryService: responseDelivery);

        await processor.HandleAsync(BuildEvent(messageId), CancellationToken.None);

        var handoff = handoffRepository.AddedHandoffs[0];
        handoff.Status.Should().Be(AgentHumanHandoffStatus.Requested, "notification failure never marks the handoff Notified");
        handoff.NotificationFailureCode.Should().Be("connector_exception");

        sessionRepository.UpdatedSessions[0].Status.Should().Be(
            AgentSessionStatus.Escalated, "a failed notification never reactivates the session — no rollback");

        responseDelivery.Calls.Should().ContainSingle();
        responseDelivery.Calls[0].Content.Should().NotContain("encaminhada", "the guest ack must never claim a notification that did not actually succeed");
    }

    [Fact]
    public async Task HandleAsync_a_restricted_intent_cancels_any_active_pending_action_without_calling_any_business_command()
    {
        var messageId = Guid.NewGuid();
        var interactionRepository = FakeAgentInteractionRepository.WithExisting(null);
        var sessionRepository = FakeAgentSessionRepository.WithExisting(NewActiveSession());
        var tool = FakeAgentTool.Succeeding("MyWriteTool", "should never run");
        var pendingAction = AgentPendingAction.Propose(
            Guid.NewGuid(), TenantId, SessionId, Guid.NewGuid(), "MyWriteTool", "{}", Now);
        var pendingActionRepository = FakeAgentPendingActionRepository.WithExisting(pendingAction);
        var request = new ModelRequest(null, [new ModelMessage(ModelMessageRole.Guest, $"polícia {FakeModelProvider.IntentTriggerPrefix}police]")]);
        var processor = CreateProcessor(
            interactionRepository, sessionRepository, request, tools: [tool], pendingActionRepository: pendingActionRepository);

        await processor.HandleAsync(BuildEvent(messageId), CancellationToken.None);

        pendingActionRepository.UpdatedPendingActions.Should().ContainSingle();
        pendingActionRepository.UpdatedPendingActions[0].Status.Should().Be(AgentPendingActionStatus.Cancelled);
        tool.LastContext.Should().BeNull("cancelling a pending action never calls any business Tool/Command");
    }

    [Fact]
    public async Task HandleAsync_low_confidence_intent_creates_a_handoff_without_any_numeric_threshold()
    {
        var messageId = Guid.NewGuid();
        var interactionRepository = FakeAgentInteractionRepository.WithExisting(null);
        var sessionRepository = FakeAgentSessionRepository.WithExisting(NewActiveSession());
        var handoffRepository = FakeAgentHumanHandoffRepository.WithExisting(null);
        var request = new ModelRequest(null, [new ModelMessage(ModelMessageRole.Guest, $"??? {FakeModelProvider.IntentTriggerPrefix}low_confidence]")]);
        var processor = CreateProcessor(interactionRepository, sessionRepository, request, handoffRepository: handoffRepository);

        await processor.HandleAsync(BuildEvent(messageId), CancellationToken.None);

        handoffRepository.AddedHandoffs.Should().ContainSingle();
        handoffRepository.AddedHandoffs[0].ReasonCode.Should().Be(AgentHumanHandoffReasonCode.LowConfidence);
    }

    [Fact]
    public async Task HandleAsync_a_low_raw_confidence_value_without_a_restricted_intent_never_triggers_a_handoff()
    {
        // Fase 11, Checkpoint 6 (mandate item 4/5): no numeric threshold
        // exists — a low Confidence value alone (with no restricted intent
        // classified) must never trigger a handoff.
        var messageId = Guid.NewGuid();
        var interactionRepository = FakeAgentInteractionRepository.WithExisting(null);
        var sessionRepository = FakeAgentSessionRepository.WithExisting(NewActiveSession());
        var handoffRepository = FakeAgentHumanHandoffRepository.WithExisting(null);
        var request = new ModelRequest(null, [new ModelMessage(ModelMessageRole.Guest, $"oi {FakeModelProvider.ConfidenceMarkerPrefix}0.01]")]);
        var processor = CreateProcessor(interactionRepository, sessionRepository, request, handoffRepository: handoffRepository);

        await processor.HandleAsync(BuildEvent(messageId), CancellationToken.None);

        handoffRepository.AddedHandoffs.Should().BeEmpty();
        sessionRepository.UpdatedSessions.Should().ContainSingle();
        sessionRepository.UpdatedSessions[0].Status.Should().Be(AgentSessionStatus.Active, "no escalation occurred — RecordInteraction just updates the session normally");
        interactionRepository.AddedInteractions[0].Confidence.Should().Be(0.01m);
    }

    [Fact]
    public async Task HandleAsync_a_new_inbound_message_on_an_already_escalated_session_never_calls_the_model_or_any_tool()
    {
        var messageId = Guid.NewGuid();
        var escalatedSession = NewActiveSession();
        escalatedSession.Escalate(Now);
        var sessionRepository = FakeAgentSessionRepository.WithExisting(escalatedSession);
        var interactionRepository = FakeAgentInteractionRepository.WithExisting(null);
        var toolExecutionRepository = new FakeAgentToolExecutionRepository();
        var tool = FakeAgentTool.Succeeding("MyTool", "should never run");
        var handoff = AgentHumanHandoff.Request(Guid.NewGuid(), TenantId, SessionId, AgentHumanHandoffReasonCode.Refund, Now);
        handoff.MarkNotified(Now);
        var handoffRepository = FakeAgentHumanHandoffRepository.WithExisting(handoff);
        var responseDelivery = FakeAgentResponseDeliveryService.Succeeding(Guid.NewGuid());
        // Even a forbidden Tool-call/confirmation-bypass marker must never be honored while Escalated.
        var request = new ModelRequest(null, [new ModelMessage(ModelMessageRole.Guest, $"oi {FakeModelProvider.ToolCallTriggerPrefix}MyTool]")]);
        var processor = CreateProcessor(
            interactionRepository, sessionRepository, request, toolExecutionRepository, [tool],
            handoffRepository: handoffRepository, responseDeliveryService: responseDelivery);

        await processor.HandleAsync(BuildEvent(messageId), CancellationToken.None);

        toolExecutionRepository.AddedExecutions.Should().BeEmpty("the suspended-session guard must never call any Tool");
        tool.LastContext.Should().BeNull();
        sessionRepository.UpdatedSessions.Should().BeEmpty("a suspended session is never touched by a subsequent inbound message");

        interactionRepository.AddedInteractions.Should().ContainSingle();
        interactionRepository.AddedInteractions[0].Outcome.Should().Be(AgentInteractionOutcome.Success);
        responseDelivery.Calls.Should().ContainSingle();
        responseDelivery.Calls[0].Content.Should().Contain("encaminhada", "the handoff was already Notified — the auto-ack may say so");
    }

    [Fact]
    public async Task HandleAsync_a_new_inbound_message_on_a_requested_but_not_yet_notified_session_never_claims_a_notification()
    {
        var messageId = Guid.NewGuid();
        var escalatedSession = NewActiveSession();
        escalatedSession.Escalate(Now);
        var sessionRepository = FakeAgentSessionRepository.WithExisting(escalatedSession);
        var interactionRepository = FakeAgentInteractionRepository.WithExisting(null);
        var handoff = AgentHumanHandoff.Request(Guid.NewGuid(), TenantId, SessionId, AgentHumanHandoffReasonCode.Accident, Now);
        var handoffRepository = FakeAgentHumanHandoffRepository.WithExisting(handoff);
        var responseDelivery = FakeAgentResponseDeliveryService.Succeeding(Guid.NewGuid());
        var request = new ModelRequest(null, [new ModelMessage(ModelMessageRole.Guest, "alguém aí?")]);
        var processor = CreateProcessor(
            interactionRepository, sessionRepository, request, handoffRepository: handoffRepository, responseDeliveryService: responseDelivery);

        await processor.HandleAsync(BuildEvent(messageId), CancellationToken.None);

        responseDelivery.Calls.Should().ContainSingle();
        responseDelivery.Calls[0].Content.Should().NotContain("encaminhada", "the handoff was never confirmed Notified — never claim it was");
    }
}
