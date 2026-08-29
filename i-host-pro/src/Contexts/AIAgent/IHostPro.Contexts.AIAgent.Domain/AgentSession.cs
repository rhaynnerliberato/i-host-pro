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
/// Status (mandate item 7): deliberately minimal — <see cref="AgentSessionStatus.Active"/>/
/// <see cref="AgentSessionStatus.Completed"/> only. Escalated/Suspended/
/// HumanOwned/Failed all belong to Checkpoint 6 (Human Handoff, Safety &amp;
/// Audit) — never anticipated here, no consumer/use case exists yet.
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
}

/// <summary>Deliberately minimal (mandate item 7) — Escalated/Suspended/HumanOwned/Failed belong to Checkpoint 6, never anticipated here.</summary>
public enum AgentSessionStatus
{
    Active,
    Completed,
}
