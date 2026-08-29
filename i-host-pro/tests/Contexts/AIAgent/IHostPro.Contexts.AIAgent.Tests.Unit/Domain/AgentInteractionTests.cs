using FluentAssertions;
using IHostPro.Contexts.AIAgent.Domain;

namespace IHostPro.Contexts.AIAgent.Tests.Unit.Domain;

/// <summary>Fase 11, Checkpoint 2 (AI Agent Foundation) — mandate item 45: success, failure, invariants. Idempotency (mandate item 36) is enforced at the repository/consumer level, not domain-level — see AgentInteractionRepository's own tests once Infrastructure is built.</summary>
public class AgentInteractionTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid AgentSessionId = Guid.NewGuid();
    private static readonly Guid InboundMessageId = Guid.NewGuid();

    private static AgentInteraction StartValid() =>
        AgentInteraction.Start(Guid.NewGuid(), TenantId, AgentSessionId, InboundMessageId, "Fake", "fake-model-v1", Now);

    [Fact]
    public void Start_with_valid_data_begins_InProgress()
    {
        var interaction = StartValid();

        interaction.TenantId.Should().Be(TenantId);
        interaction.AgentSessionId.Should().Be(AgentSessionId);
        interaction.InboundMessageId.Should().Be(InboundMessageId);
        interaction.ModelProvider.Should().Be("Fake");
        interaction.ModelName.Should().Be("fake-model-v1");
        interaction.StartedAtUtc.Should().Be(Now);
        interaction.Outcome.Should().Be(AgentInteractionOutcome.InProgress);
        interaction.CompletedAtUtc.Should().BeNull();
    }

    [Fact]
    public void Start_rejects_empty_AgentSessionId()
    {
        var act = () => AgentInteraction.Start(Guid.NewGuid(), TenantId, Guid.Empty, InboundMessageId, "Fake", "fake-model-v1", Now);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Start_rejects_empty_InboundMessageId()
    {
        var act = () => AgentInteraction.Start(Guid.NewGuid(), TenantId, AgentSessionId, Guid.Empty, "Fake", "fake-model-v1", Now);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void CompleteSuccessfully_records_audit_metadata_and_Success_outcome()
    {
        var interaction = StartValid();
        var completedAt = Now.AddSeconds(2);

        interaction.CompleteSuccessfully(completedAt, intent: null, language: "pt-BR", confidence: null, inputTokens: 42, outputTokens: 17);

        interaction.Outcome.Should().Be(AgentInteractionOutcome.Success);
        interaction.Language.Should().Be("pt-BR");
        interaction.InputTokens.Should().Be(42);
        interaction.OutputTokens.Should().Be(17);
        interaction.CompletedAtUtc.Should().Be(completedAt);
    }

    [Fact]
    public void CompleteWithFailure_records_Failure_outcome_with_no_token_metadata()
    {
        var interaction = StartValid();
        var completedAt = Now.AddSeconds(1);

        interaction.CompleteWithFailure(completedAt);

        interaction.Outcome.Should().Be(AgentInteractionOutcome.Failure);
        interaction.CompletedAtUtc.Should().Be(completedAt);
        interaction.InputTokens.Should().Be(0);
        interaction.OutputTokens.Should().Be(0);
    }

    [Fact]
    public void CompleteSuccessfully_throws_when_already_completed()
    {
        var interaction = StartValid();
        interaction.CompleteWithFailure(Now.AddSeconds(1));

        var act = () => interaction.CompleteSuccessfully(Now.AddSeconds(2), null, null, null, 1, 1);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void CompleteWithFailure_throws_when_already_completed()
    {
        var interaction = StartValid();
        interaction.CompleteSuccessfully(Now.AddSeconds(1), null, null, null, 1, 1);

        var act = () => interaction.CompleteWithFailure(Now.AddSeconds(2));

        act.Should().Throw<InvalidOperationException>();
    }

    // ---- Confidence (mandate item 35: normalized decimal, 0..1 inclusive) ----

    [Theory]
    [InlineData(null)]
    [InlineData(0.0)]
    [InlineData(1.0)]
    [InlineData(0.9)]
    public void CompleteSuccessfully_accepts_null_or_0_to_1_inclusive_confidence(double? value)
    {
        var confidence = value.HasValue ? (decimal?)value.Value : null;
        var interaction = StartValid();

        interaction.CompleteSuccessfully(Now.AddSeconds(1), null, null, confidence, 1, 1);

        interaction.Confidence.Should().Be(confidence);
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(1.01)]
    [InlineData(-1)]
    [InlineData(2)]
    public void CompleteSuccessfully_rejects_out_of_range_confidence_and_never_clamps(double value)
    {
        var interaction = StartValid();

        var act = () => interaction.CompleteSuccessfully(Now.AddSeconds(1), null, null, (decimal)value, 1, 1);

        act.Should().Throw<ArgumentOutOfRangeException>();
        interaction.Outcome.Should().Be(AgentInteractionOutcome.InProgress, "an invariant violation must never partially apply the completion");
    }
}
