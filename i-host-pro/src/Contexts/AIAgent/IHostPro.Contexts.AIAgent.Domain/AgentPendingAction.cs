using IHostPro.BuildingBlocks.Domain;

namespace IHostPro.Contexts.AIAgent.Domain;

/// <summary>
/// A write Tool the model proposed but has not yet been executed, awaiting
/// the guest's own conversational confirmation before the real Application
/// Command runs (Fase 11, Checkpoint 4 — Write Tools &amp; Response Delivery,
/// translating the CP0 <c>WriteConfirmation=REQUIRED</c> decision into a
/// technical model). References <see cref="AgentSessionId"/>/
/// <see cref="ProposedByInteractionId"/> by opaque id only, mirroring
/// <see cref="AgentInteraction"/>'s own precedent for <see cref="AgentSession"/> —
/// but, like <see cref="AgentToolExecution"/>'s own established exception,
/// gets a real database foreign key to <c>agent_interactions</c> since both
/// live in the same <c>ai_agent</c> schema/Bounded Context.
///
/// <see cref="SanitizedArguments"/> is a small, application-controlled JSON
/// payload — never a raw dump of whatever the model supplied. Each
/// confirmable Tool (<see cref="IHostPro.Contexts.AIAgent.Application.Tools.IConfirmableAgentTool"/>)
/// validates and narrows the model's own arguments down to exactly the
/// fields it will need to re-execute later.
///
/// Lifecycle: <see cref="Propose"/> → <see cref="Confirm"/> → <see cref="MarkExecuted"/>,
/// or <see cref="Propose"/> → <see cref="Cancel"/> — both
/// <see cref="AgentPendingActionStatus.Executed"/> and
/// <see cref="AgentPendingActionStatus.Cancelled"/> are terminal. No expiry/
/// TTL exists (CP4 mandate item 13, explicit decision) — a pending action
/// remains valid for as long as its own <see cref="AgentSession"/> stays
/// <see cref="AgentSessionStatus.Active"/>; the Application layer enforces
/// this by construction (a session's own resolver never reuses a Completed
/// session), never a field on this entity.
/// </summary>
public sealed class AgentPendingAction : AggregateRoot<Guid>, ITenantOwned
{
    public Guid TenantId { get; private set; }
    public Guid AgentSessionId { get; private set; }
    public Guid ProposedByInteractionId { get; private set; }
    public string ToolName { get; private set; } = null!;
    public string SanitizedArguments { get; private set; } = null!;
    public AgentPendingActionStatus Status { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset? ConfirmedAtUtc { get; private set; }
    public DateTimeOffset? ExecutedAtUtc { get; private set; }
    public DateTimeOffset? CancelledAtUtc { get; private set; }

    private AgentPendingAction()
    {
        // EF Core materialization.
    }

    private AgentPendingAction(
        Guid id, Guid tenantId, Guid agentSessionId, Guid proposedByInteractionId,
        string toolName, string sanitizedArguments, DateTimeOffset now)
        : base(id)
    {
        TenantId = tenantId;
        AgentSessionId = agentSessionId;
        ProposedByInteractionId = proposedByInteractionId;
        ToolName = toolName;
        SanitizedArguments = sanitizedArguments;
        Status = AgentPendingActionStatus.Proposed;
        CreatedAtUtc = now;
    }

    public static AgentPendingAction Propose(
        Guid id, Guid tenantId, Guid agentSessionId, Guid proposedByInteractionId,
        string toolName, string sanitizedArguments, DateTimeOffset now)
    {
        if (agentSessionId == Guid.Empty)
            throw new ArgumentException("Agent session id cannot be empty.", nameof(agentSessionId));

        if (proposedByInteractionId == Guid.Empty)
            throw new ArgumentException("Proposed-by interaction id cannot be empty.", nameof(proposedByInteractionId));

        if (string.IsNullOrWhiteSpace(toolName))
            throw new ArgumentException("Tool name cannot be empty.", nameof(toolName));

        if (string.IsNullOrWhiteSpace(sanitizedArguments))
            throw new ArgumentException("Sanitized arguments cannot be empty.", nameof(sanitizedArguments));

        return new AgentPendingAction(id, tenantId, agentSessionId, proposedByInteractionId, toolName, sanitizedArguments, now);
    }

    public void Confirm(DateTimeOffset now)
    {
        if (Status != AgentPendingActionStatus.Proposed)
            throw new InvalidOperationException($"Cannot confirm a pending action in status '{Status}'.");

        Status = AgentPendingActionStatus.Confirmed;
        ConfirmedAtUtc = now;
    }

    public void MarkExecuted(DateTimeOffset now)
    {
        if (Status != AgentPendingActionStatus.Confirmed)
            throw new InvalidOperationException($"Cannot execute a pending action in status '{Status}'.");

        Status = AgentPendingActionStatus.Executed;
        ExecutedAtUtc = now;
    }

    /// <summary>Cancels the still-unexecuted proposal itself — never calls any business Command (CP4 mandate item 16).</summary>
    public void Cancel(DateTimeOffset now)
    {
        if (Status is AgentPendingActionStatus.Executed or AgentPendingActionStatus.Cancelled)
            throw new InvalidOperationException($"Cannot cancel a pending action in status '{Status}'.");

        Status = AgentPendingActionStatus.Cancelled;
        CancelledAtUtc = now;
    }
}

/// <summary>
/// No <c>Expired</c> value (CP4 mandate item 12) — no TTL is documented
/// anywhere in the source of truth, and inventing one was explicitly
/// rejected. <see cref="Proposed"/>/<see cref="Confirmed"/> are the two
/// "active" states (at most one per <see cref="AgentSession"/>, enforced by
/// a partial unique index at the Infrastructure layer);
/// <see cref="Executed"/>/<see cref="Cancelled"/> are terminal.
/// </summary>
public enum AgentPendingActionStatus
{
    Proposed,
    Confirmed,
    Executed,
    Cancelled,
}
