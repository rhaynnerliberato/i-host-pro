using IHostPro.BuildingBlocks.Domain;

namespace IHostPro.Contexts.Communication.Domain;

/// <summary>
/// A guest communication thread, owned exclusively by Communication
/// (Documento 12 §9 — "Conversa... representa um canal de atendimento";
/// Fase 11, Checkpoint 1 — Inbound Conversation Foundation).
///
/// Origin (Documento 12 §17 — "nenhuma conversa existe sem uma origem"): in
/// this checkpoint, origin is always <see cref="ReservationId"/> — a
/// Conversation is never created without one already resolved (0/N-candidate
/// inbound messages never reach this constructor at all, mandate item 16).
///
/// Cardinality: one active Conversation per (TenantId, ReservationId,
/// Channel) — mandate item 19's own default preference, enforced by a unique
/// partial index (see <c>ConversationConfiguration</c>). No archive/reopen
/// semantics exist; <see cref="ConversationStatus.Active"/> remains the only
/// state (Fase 11, Checkpoint 6 official decision —
/// <c>ConversationStatusChanged=false</c>): "the AI is suspended for a human
/// handoff" is a fact about <c>AIAgent.AgentSession</c>, never duplicated
/// here — a Conversation is a message channel, independent of who is
/// currently driving it.
///
/// Deliberately carries no AI-related state (no intent, no confidence, no
/// model, no prompt) — that belongs to the future AI Agent Bounded Context's
/// own <c>AISession</c> (Checkpoint 2), a distinct concept referencing this
/// Conversation only by opaque id, never merged into it (Documento 12 §10).
/// </summary>
public sealed class Conversation : AggregateRoot<Guid>, ITenantOwned
{
    public Guid TenantId { get; private set; }
    public Guid ReservationId { get; private set; }
    public string Channel { get; private set; } = null!;
    public ConversationStatus Status { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset UpdatedAtUtc { get; private set; }
    public DateTimeOffset LastMessageAtUtc { get; private set; }

    private Conversation()
    {
        // EF Core materialization.
    }

    private Conversation(
        Guid id, Guid tenantId, Guid reservationId, string channel, DateTimeOffset createdAtUtc)
        : base(id)
    {
        TenantId = tenantId;
        ReservationId = reservationId;
        Channel = channel;
        Status = ConversationStatus.Active;
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = createdAtUtc;
        LastMessageAtUtc = createdAtUtc;
    }

    public static Conversation Create(
        Guid id, Guid tenantId, Guid reservationId, string channel, DateTimeOffset createdAtUtc)
    {
        if (string.IsNullOrWhiteSpace(channel))
            throw new ArgumentException("Channel cannot be empty.", nameof(channel));

        return new Conversation(id, tenantId, reservationId, channel, createdAtUtc);
    }

    /// <summary>Called once per Message (inbound or outbound) attached to this Conversation — never a business decision on its own, purely a denormalized read-convenience field.</summary>
    public void RecordMessageAt(DateTimeOffset occurredAtUtc)
    {
        LastMessageAtUtc = occurredAtUtc;
        UpdatedAtUtc = occurredAtUtc;
    }
}

/// <summary>Deliberately a single value (Fase 11, Checkpoint 6 official decision, <c>ConversationStatusChanged=false</c>) — human-handoff state lives exclusively in <c>AIAgent.AgentSessionStatus.Escalated</c>, never duplicated here.</summary>
public enum ConversationStatus
{
    Active,
}
