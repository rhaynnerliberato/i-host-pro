using IHostPro.BuildingBlocks.Domain;

namespace IHostPro.Contexts.AIAgent.Domain;

/// <summary>
/// A single audited model round-trip against an <see cref="AgentSession"/>
/// (Fase 11, Checkpoint 2 — AI Agent Foundation; Documento 16 §24 —
/// "toda interação deverá registrar" intenção/decisões/custo/modelo).
/// References <see cref="AgentSessionId"/>/<see cref="InboundMessageId"/> by
/// opaque id only — <see cref="InboundMessageId"/> is Communication's own
/// <c>Message.Id</c>, never a cross-context FK.
///
/// Idempotency (mandate item 36): the business key is
/// <see cref="TenantId"/> + <see cref="InboundMessageId"/> — the same
/// <c>ConversationMessageReceived</c>/<c>MessageId</c> must never produce a
/// second <see cref="AgentInteraction"/> (lookup-before-create in the
/// consumer, unique index as defense-in-depth — see the Infrastructure
/// mapping).
///
/// Deliberately does NOT persist a full response/prompt text field
/// (governance decision, Fase 11 CP2 §10: Documento 16 §24 audits "resposta
/// enviada" — CP2 never sends anything to the guest, so a model output is
/// not yet a "resposta enviada"; CP4, once real delivery exists, will link
/// to Communication's own <c>Message</c> rather than duplicate its body
/// here) — only the audit metadata Documento 16 §24 requires without
/// ambiguity (intent, language, confidence, model, token counts, outcome).
///
/// Confidence: normalized <c>decimal?</c>, <c>0..1</c> inclusive when
/// non-null — see <see cref="AgentSession"/>'s own doc comment.
/// </summary>
public sealed class AgentInteraction : AggregateRoot<Guid>, ITenantOwned
{
    public Guid TenantId { get; private set; }
    public Guid AgentSessionId { get; private set; }
    public Guid InboundMessageId { get; private set; }
    public string? Intent { get; private set; }
    public string? Language { get; private set; }
    public decimal? Confidence { get; private set; }
    public string ModelProvider { get; private set; } = null!;
    public string ModelName { get; private set; } = null!;
    public int InputTokens { get; private set; }
    public int OutputTokens { get; private set; }
    public DateTimeOffset StartedAtUtc { get; private set; }
    public DateTimeOffset? CompletedAtUtc { get; private set; }
    public AgentInteractionOutcome Outcome { get; private set; }

    private AgentInteraction()
    {
        // EF Core materialization.
    }

    private AgentInteraction(
        Guid id, Guid tenantId, Guid agentSessionId, Guid inboundMessageId,
        string modelProvider, string modelName, DateTimeOffset startedAtUtc)
        : base(id)
    {
        TenantId = tenantId;
        AgentSessionId = agentSessionId;
        InboundMessageId = inboundMessageId;
        ModelProvider = modelProvider;
        ModelName = modelName;
        StartedAtUtc = startedAtUtc;
        Outcome = AgentInteractionOutcome.InProgress;
    }

    public static AgentInteraction Start(
        Guid id, Guid tenantId, Guid agentSessionId, Guid inboundMessageId,
        string modelProvider, string modelName, DateTimeOffset startedAtUtc)
    {
        if (agentSessionId == Guid.Empty)
            throw new ArgumentException("Agent session id cannot be empty.", nameof(agentSessionId));

        if (inboundMessageId == Guid.Empty)
            throw new ArgumentException("Inbound message id cannot be empty.", nameof(inboundMessageId));

        if (string.IsNullOrWhiteSpace(modelProvider))
            throw new ArgumentException("Model provider cannot be empty.", nameof(modelProvider));

        if (string.IsNullOrWhiteSpace(modelName))
            throw new ArgumentException("Model name cannot be empty.", nameof(modelName));

        return new AgentInteraction(id, tenantId, agentSessionId, inboundMessageId, modelProvider, modelName, startedAtUtc);
    }

    public void CompleteSuccessfully(
        DateTimeOffset completedAtUtc, string? intent, string? language, decimal? confidence,
        int inputTokens, int outputTokens)
    {
        if (Outcome != AgentInteractionOutcome.InProgress)
            throw new InvalidOperationException($"Cannot complete an interaction already in outcome '{Outcome}'.");

        ConfidenceGuard.EnsureValid(confidence);

        Intent = intent;
        Language = language;
        Confidence = confidence;
        InputTokens = inputTokens;
        OutputTokens = outputTokens;
        CompletedAtUtc = completedAtUtc;
        Outcome = AgentInteractionOutcome.Success;
    }

    public void CompleteWithFailure(DateTimeOffset completedAtUtc)
    {
        if (Outcome != AgentInteractionOutcome.InProgress)
            throw new InvalidOperationException($"Cannot complete an interaction already in outcome '{Outcome}'.");

        CompletedAtUtc = completedAtUtc;
        Outcome = AgentInteractionOutcome.Failure;
    }
}

/// <summary>Mandate item 35 — CP2 only needs to distinguish a deterministic Fake-provider success from a controlled failure; no partial/timeout/rate-limit taxonomy exists yet (real-provider failure modes are Checkpoint 7's scope).</summary>
public enum AgentInteractionOutcome
{
    InProgress,
    Success,
    Failure,
}
