using IHostPro.BuildingBlocks.Domain;

namespace IHostPro.Contexts.AIAgent.Domain;

/// <summary>
/// The AI Agent Bounded Context's own orchestration state for a single guest
/// interaction thread (Fase 11, Checkpoint 2 — AI Agent Foundation),
/// deliberately distinct from Communication's own <c>Conversation</c>
/// (Documento 12 §9/§10 — "Conversa" vs. "Sessão IA" are two separate
/// concepts, never merged). References <see cref="ConversationId"/>/
/// <see cref="ReservationId"/> by opaque id only — no cross-context FK, no
/// direct reference to Communication.Domain/Reservations.Domain (mandate
/// item 5, Architecture Principles' own dependency rules).
///
/// Cardinality (mandate item 6): no explicit rule was found governing
/// concurrent AgentSessions per Conversation in Documento 12 §10 (it
/// describes only what a "Sessão IA" stores, not how many may be active at
/// once) — the MVP fallback the mandate itself provides applies: one active
/// <see cref="AgentSession"/> per <see cref="ConversationId"/>, mirroring
/// <c>Conversation</c>'s own cardinality precedent (CP1) exactly, enforced
/// by a unique partial index (see the Infrastructure mapping).
///
/// Status: <see cref="AgentSessionStatus.Active"/>/<see cref="AgentSessionStatus.Completed"/>
/// (Checkpoint 2), extended by Checkpoint 6 (Human Handoff, Safety &amp;
/// Audit) with <see cref="AgentSessionStatus.Escalated"/> — the sole owner of
/// "the AI is currently suspended for this session" (never duplicated onto
/// <c>Communication.Conversation.Status</c>, which remains <c>Active</c>-only
/// — a Conversation is a message channel, independent of who is driving it).
/// <see cref="Escalate"/>/<see cref="Resume"/> transition only
/// Active↔Escalated; a <see cref="AgentSessionStatus.Completed"/> session is
/// never reopened by either.
///
/// Confidence (Fase 11, Checkpoint 2 governance resolution): normalized
/// <c>decimal?</c>, <c>0..1</c> inclusive when non-null, <see langword="null"/>
/// meaning "provider did not supply one" — never clamped, an out-of-range
/// value is an invariant violation (<see cref="ConfidenceGuard.EnsureValid"/>).
/// No business threshold (e.g. handoff at 0.7) is decided here — that is
/// Checkpoint 6/Policy's future scope.
/// </summary>
public sealed class AgentSession : AggregateRoot<Guid>, ITenantOwned
{
    public Guid TenantId { get; private set; }
    public Guid ConversationId { get; private set; }
    public Guid ReservationId { get; private set; }
    public AgentSessionStatus Status { get; private set; }
    public string? Language { get; private set; }
    public string? Intent { get; private set; }
    public decimal? Confidence { get; private set; }
    public string? ModelProvider { get; private set; }
    public string? ModelName { get; private set; }
    public DateTimeOffset StartedAtUtc { get; private set; }
    public DateTimeOffset UpdatedAtUtc { get; private set; }
    public DateTimeOffset? LastInteractionAtUtc { get; private set; }
    public DateTimeOffset? EndedAtUtc { get; private set; }

    private AgentSession()
    {
        // EF Core materialization.
    }

    private AgentSession(Guid id, Guid tenantId, Guid conversationId, Guid reservationId, DateTimeOffset now)
        : base(id)
    {
        TenantId = tenantId;
        ConversationId = conversationId;
        ReservationId = reservationId;
        Status = AgentSessionStatus.Active;
        StartedAtUtc = now;
        UpdatedAtUtc = now;
    }

    public static AgentSession Create(Guid id, Guid tenantId, Guid conversationId, Guid reservationId, DateTimeOffset now)
    {
        if (conversationId == Guid.Empty)
            throw new ArgumentException("Conversation id cannot be empty.", nameof(conversationId));

        if (reservationId == Guid.Empty)
            throw new ArgumentException("Reservation id cannot be empty.", nameof(reservationId));

        return new AgentSession(id, tenantId, conversationId, reservationId, now);
    }

    /// <summary>
    /// Records the orchestration metadata produced by a single interaction
    /// against this session (mandate item 5) — never a business decision on
    /// its own, updates only the fields the caller actually resolved this
    /// turn (a <see langword="null"/> argument leaves the prior value
    /// untouched, mirroring <c>Conversation.RecordMessageAt</c>'s own
    /// denormalized-field-update pattern).
    /// </summary>
    public void RecordInteraction(
        DateTimeOffset occurredAtUtc, string? language, string? intent, decimal? confidence,
        string? modelProvider, string? modelName)
    {
        if (Status != AgentSessionStatus.Active)
            throw new InvalidOperationException($"Cannot record an interaction against a session in status '{Status}'.");

        ConfidenceGuard.EnsureValid(confidence);

        LastInteractionAtUtc = occurredAtUtc;
        UpdatedAtUtc = occurredAtUtc;
        Language = language ?? Language;
        Intent = intent ?? Intent;
        Confidence = confidence ?? Confidence;
        ModelProvider = modelProvider ?? ModelProvider;
        ModelName = modelName ?? ModelName;
    }

    /// <summary>Idempotent no-op when already <see cref="AgentSessionStatus.Completed"/> — mirrors <c>PixCharge</c>'s own terminal-state idempotency precedent.</summary>
    public void Complete(DateTimeOffset now)
    {
        if (Status == AgentSessionStatus.Completed)
            return;

        Status = AgentSessionStatus.Completed;
        EndedAtUtc = now;
        UpdatedAtUtc = now;
    }

    /// <summary>
    /// Suspends automated processing for this session (Fase 11, Checkpoint 6)
    /// — the orchestrator must never call <see cref="IHostPro.Contexts.AIAgent.Application.IModelProvider"/>
    /// or any Tool while <see cref="Status"/> is <see cref="AgentSessionStatus.Escalated"/>.
    /// Only a real <see cref="AgentHumanHandoff"/> escalates a session — this
    /// method never runs standalone.
    /// </summary>
    public void Escalate(DateTimeOffset now)
    {
        if (Status != AgentSessionStatus.Active)
            throw new InvalidOperationException($"Cannot escalate a session in status '{Status}'.");

        Status = AgentSessionStatus.Escalated;
        UpdatedAtUtc = now;
    }

    /// <summary>Manual-only (CP0 decision, reaffirmed by CP6 mandate item 41) — never auto-resumed by timeout, notification outcome, or a new guest message.</summary>
    public void Resume(DateTimeOffset now)
    {
        if (Status != AgentSessionStatus.Escalated)
            throw new InvalidOperationException($"Cannot resume a session in status '{Status}'.");

        Status = AgentSessionStatus.Active;
        UpdatedAtUtc = now;
    }
}

/// <summary>
/// <see cref="Escalated"/> (Fase 11, Checkpoint 6) is the sole owner of "the
/// AI is currently suspended for a real human handoff" — never duplicated
/// onto <c>Communication.Conversation.Status</c>. No <c>Suspended</c>/
/// <c>HumanOwned</c>/<c>Failed</c> value exists — one restricted-intent state
/// is sufficient for this checkpoint's scope (manual Resume only, no
/// assignment/acknowledgement queue).
/// </summary>
public enum AgentSessionStatus
{
    Active,
    Completed,
    Escalated,
}
