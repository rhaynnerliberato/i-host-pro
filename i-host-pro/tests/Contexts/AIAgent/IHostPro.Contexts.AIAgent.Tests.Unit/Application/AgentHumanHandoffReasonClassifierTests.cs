using FluentAssertions;
using IHostPro.Contexts.AIAgent.Application;
using IHostPro.Contexts.AIAgent.Domain;

namespace IHostPro.Contexts.AIAgent.Tests.Unit.Application;

/// <summary>Fase 11, Checkpoint 6 — the fixed, closed Intent -&gt; AgentHumanHandoffReasonCode allowlist (mandate item 2/7/46).</summary>
public class AgentHumanHandoffReasonClassifierTests
{
    private readonly AgentHumanHandoffReasonClassifier _classifier = new();

    [Theory]
    [InlineData("human_handoff_requested", AgentHumanHandoffReasonCode.ExplicitHumanRequest)]
    [InlineData("refund", AgentHumanHandoffReasonCode.Refund)]
    [InlineData("accident", AgentHumanHandoffReasonCode.Accident)]
    [InlineData("police", AgentHumanHandoffReasonCode.Police)]
    [InlineData("negotiation", AgentHumanHandoffReasonCode.Negotiation)]
    [InlineData("severe_damage", AgentHumanHandoffReasonCode.SevereDamage)]
    [InlineData("serious_complaint", AgentHumanHandoffReasonCode.SeriousComplaint)]
    [InlineData("aggressive_behavior", AgentHumanHandoffReasonCode.AggressiveBehavior)]
    [InlineData("low_confidence", AgentHumanHandoffReasonCode.LowConfidence)]
    [InlineData("integration_failure", AgentHumanHandoffReasonCode.IntegrationFailure)]
    public void Classify_maps_every_allowlisted_intent_to_its_reason_code(string intent, AgentHumanHandoffReasonCode expected)
    {
        _classifier.Classify(intent).Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("unsupported_request")]
    [InlineData("greeting")]
    [InlineData("Refund")]
    [InlineData("REFUND")]
    public void Classify_returns_null_for_anything_outside_the_fixed_allowlist(string? intent)
    {
        // "unsupported_request" (Checkpoint 5) never triggers a handoff — it
        // is a benign, safely-refused request, not a restricted one.
        // Case-sensitive: the model must produce the exact lowercase value.
        _classifier.Classify(intent).Should().BeNull();
    }
}
