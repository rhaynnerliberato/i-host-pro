using FluentAssertions;
using IHostPro.Contexts.AIAgent.Domain;

namespace IHostPro.Contexts.AIAgent.Tests.Unit.Domain;

/// <summary>Fase 11, Checkpoint 2 (AI Agent Foundation) — mandate item 45: create, reuse active session, timestamps/invariants.</summary>
public class AgentSessionTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid ConversationId = Guid.NewGuid();
    private static readonly Guid ReservationId = Guid.NewGuid();

    private static AgentSession CreateValid() =>
        AgentSession.Create(Guid.NewGuid(), TenantId, ConversationId, ReservationId, Now);

    [Fact]
    public void Create_with_valid_data_starts_as_Active_with_no_interaction_metadata()
    {
        var session = CreateValid();

        session.TenantId.Should().Be(TenantId);
        session.ConversationId.Should().Be(ConversationId);
        session.ReservationId.Should().Be(ReservationId);
        session.Status.Should().Be(AgentSessionStatus.Active);
        session.Language.Should().BeNull();
        session.Intent.Should().BeNull();
        session.Confidence.Should().BeNull();
        session.ModelProvider.Should().BeNull();
        session.ModelName.Should().BeNull();
        session.StartedAtUtc.Should().Be(Now);
        session.UpdatedAtUtc.Should().Be(Now);
        session.LastInteractionAtUtc.Should().BeNull();
        session.EndedAtUtc.Should().BeNull();
    }

    [Fact]
    public void Create_rejects_empty_ConversationId()
    {
        var act = () => AgentSession.Create(Guid.NewGuid(), TenantId, Guid.Empty, ReservationId, Now);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_rejects_empty_ReservationId()
    {
        var act = () => AgentSession.Create(Guid.NewGuid(), TenantId, ConversationId, Guid.Empty, Now);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void RecordInteraction_updates_only_the_provided_fields_and_timestamps()
    {
        var session = CreateValid();
        var interactionAt = Now.AddMinutes(1);

        session.RecordInteraction(interactionAt, language: "pt-BR", intent: null, confidence: null, modelProvider: "Fake", modelName: "fake-model-v1");

        session.Language.Should().Be("pt-BR");
        session.Intent.Should().BeNull();
        session.ModelProvider.Should().Be("Fake");
        session.ModelName.Should().Be("fake-model-v1");
        session.LastInteractionAtUtc.Should().Be(interactionAt);
        session.UpdatedAtUtc.Should().Be(interactionAt);
    }

    [Fact]
    public void RecordInteraction_never_overwrites_a_previously_recorded_field_with_null()
    {
        var session = CreateValid();
        session.RecordInteraction(Now.AddMinutes(1), language: "pt-BR", intent: "greeting", confidence: null, modelProvider: "Fake", modelName: "fake-model-v1");

        session.RecordInteraction(Now.AddMinutes(2), language: null, intent: null, confidence: null, modelProvider: null, modelName: null);

        session.Language.Should().Be("pt-BR");
        session.Intent.Should().Be("greeting");
        session.ModelProvider.Should().Be("Fake");
        session.ModelName.Should().Be("fake-model-v1");
    }

    [Fact]
    public void RecordInteraction_throws_when_session_already_Completed()
    {
        var session = CreateValid();
        session.Complete(Now.AddMinutes(1));

        var act = () => session.RecordInteraction(Now.AddMinutes(2), "pt-BR", null, null, "Fake", "fake-model-v1");

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Complete_sets_EndedAtUtc_and_Status()
    {
        var session = CreateValid();
        var endedAt = Now.AddMinutes(5);

        session.Complete(endedAt);

        session.Status.Should().Be(AgentSessionStatus.Completed);
        session.EndedAtUtc.Should().Be(endedAt);
        session.UpdatedAtUtc.Should().Be(endedAt);
    }

    [Fact]
    public void Complete_is_idempotent_and_keeps_the_first_EndedAtUtc()
    {
        var session = CreateValid();
        var firstEnd = Now.AddMinutes(5);
        session.Complete(firstEnd);

        session.Complete(Now.AddMinutes(10));

        session.EndedAtUtc.Should().Be(firstEnd);
    }

    // ---- Confidence (mandate item 35: normalized decimal, 0..1 inclusive) ----

    [Theory]
    [InlineData(null)]
    [InlineData(0.0)]
    [InlineData(1.0)]
    [InlineData(0.5)]
    public void RecordInteraction_accepts_null_or_0_to_1_inclusive_confidence(double? value)
    {
        var confidence = value.HasValue ? (decimal?)value.Value : null;
        var session = CreateValid();

        session.RecordInteraction(Now.AddMinutes(1), null, null, confidence, "Fake", "fake-model-v1");

        session.Confidence.Should().Be(confidence);
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(1.01)]
    [InlineData(-1)]
    [InlineData(2)]
    public void RecordInteraction_rejects_out_of_range_confidence_and_never_clamps(double value)
    {
        var session = CreateValid();

        var act = () => session.RecordInteraction(Now.AddMinutes(1), null, null, (decimal)value, "Fake", "fake-model-v1");

        act.Should().Throw<ArgumentOutOfRangeException>();
        session.Confidence.Should().BeNull("an invariant violation must never partially apply — the confidence value must never be clamped or silently stored");
    }
}
