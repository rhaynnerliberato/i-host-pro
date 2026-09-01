using IHostPro.Contexts.AIAgent.Domain;

namespace IHostPro.Contexts.AIAgent.Application;

/// <summary>
/// The safety classifier (Fase 11, Checkpoint 6, mandate item 3/33/44): maps
/// <see cref="ModelResult.Intent"/> (a free, provider-neutral string the
/// model supplies) to an <see cref="AgentHumanHandoffReasonCode"/> via a
/// fixed, explicit, server-side table — never dynamic/reflection-based,
/// never a code the model provides directly. The model classifies intent;
/// it never decides whether that intent triggers a handoff, nor which
/// <see cref="AgentHumanHandoffReasonCode"/> applies — this class alone owns
/// that mapping, mirroring exactly how <see cref="Tools.IAgentToolConfirmationPolicy"/>
/// alone decides whether a Tool requires confirmation (CP4's own precedent).
/// An unrecognized intent (including <c>"unsupported_request"</c>, CP5) never
/// triggers a handoff — <see langword="null"/> means "no restricted intent
/// classified this turn."
/// </summary>
public interface IAgentHumanHandoffReasonClassifier
{
    AgentHumanHandoffReasonCode? Classify(string? intent);
}

/// <summary>
/// The fixed, closed mapping (CP6 mandate item 46) — intent values chosen to
/// read naturally alongside <see cref="Tools.FakeModelProvider"/>-style
/// marker constants a future real provider would also emit.
/// <c>"human_handoff_requested"</c> is reused verbatim from Checkpoint 5,
/// which already classified it but took no handoff action on it — CP6 is
/// its first real consumer.
/// </summary>
public sealed class AgentHumanHandoffReasonClassifier : IAgentHumanHandoffReasonClassifier
{
    private static readonly IReadOnlyDictionary<string, AgentHumanHandoffReasonCode> IntentToReasonCode =
        new Dictionary<string, AgentHumanHandoffReasonCode>(StringComparer.Ordinal)
        {
            ["human_handoff_requested"] = AgentHumanHandoffReasonCode.ExplicitHumanRequest,
            ["refund"] = AgentHumanHandoffReasonCode.Refund,
            ["accident"] = AgentHumanHandoffReasonCode.Accident,
            ["police"] = AgentHumanHandoffReasonCode.Police,
            ["negotiation"] = AgentHumanHandoffReasonCode.Negotiation,
            ["severe_damage"] = AgentHumanHandoffReasonCode.SevereDamage,
            ["serious_complaint"] = AgentHumanHandoffReasonCode.SeriousComplaint,
            ["aggressive_behavior"] = AgentHumanHandoffReasonCode.AggressiveBehavior,
            ["low_confidence"] = AgentHumanHandoffReasonCode.LowConfidence,
            ["integration_failure"] = AgentHumanHandoffReasonCode.IntegrationFailure,
        };

    public AgentHumanHandoffReasonCode? Classify(string? intent) =>
        intent is not null && IntentToReasonCode.TryGetValue(intent, out var reasonCode) ? reasonCode : null;
}
