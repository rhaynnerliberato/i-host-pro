using FluentAssertions;
using IHostPro.Contexts.AIAgent.Application;
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
        ModelRequest? request = null) =>
        new(
            FakeAgentSessionResolver.Returning(SessionId), sessionRepository, interactionRepository,
            FakeAgentContextBuilder.Returning(request ?? new ModelRequest(null, [new ModelMessage(ModelMessageRole.Guest, "Olá")])),
            new FakeModelProvider(NullLogger<FakeModelProvider>.Instance),
            new PassThroughAIAgentTransactionExecutor(), TimeProvider.System,
            NullLogger<ConversationMessageReceivedProcessor>.Instance);

    [Fact]
    public async Task HandleAsync_success_persists_an_AgentInteraction_and_updates_session_metadata()
    {
        var messageId = Guid.NewGuid();
        var interactionRepository = FakeAgentInteractionRepository.WithExisting(null);
        var sessionRepository = FakeAgentSessionRepository.WithExisting(NewActiveSession());
        var processor = CreateProcessor(interactionRepository, sessionRepository);

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

        sessionRepository.UpdatedSessions.Should().ContainSingle();
        sessionRepository.UpdatedSessions[0].Language.Should().Be("pt-BR");
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
        var request = new ModelRequest(null, [new ModelMessage(ModelMessageRole.Guest, $"oi {FakeModelProvider.FailureTriggerMarker}")]);
        var processor = CreateProcessor(interactionRepository, sessionRepository, request);

        await processor.HandleAsync(BuildEvent(messageId), CancellationToken.None);

        interactionRepository.AddedInteractions.Should().ContainSingle();
        var interaction = interactionRepository.AddedInteractions[0];
        interaction.Outcome.Should().Be(AgentInteractionOutcome.Failure);
        interaction.InputTokens.Should().Be(0);
        interaction.OutputTokens.Should().Be(0);

        sessionRepository.UpdatedSessions.Should().BeEmpty(
            "a failed interaction has no confirmed language/intent/confidence to record — the session remains consistent, untouched");
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
}
