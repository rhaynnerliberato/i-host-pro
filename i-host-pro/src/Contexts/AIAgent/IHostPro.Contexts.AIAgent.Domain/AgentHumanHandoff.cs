using IHostPro.BuildingBlocks.Domain;

namespace IHostPro.Contexts.AIAgent.Domain;

/// <summary>
/// A real escalation from automated AI processing to human attention (Fase
/// 11, Checkpoint 6 — Human Handoff, Safety &amp; Audit), translating the CP0
/// <c>HumanHandoffResume=MANUAL ONLY</c> decision and Documento 17 Workflow
/// 14 ("Escalonamento para Humano") into a technical model. References
/// <see cref="AgentSessionId"/> by opaque id only, mirroring
/// <see cref="AgentPendingAction"/>'s own precedent — but, like
/// <see cref="AgentToolExecution"/>/<see cref="AgentPendingAction"/>'s own
/// established exception, gets a real database foreign key to
/// <c>agent_sessions</c> since both live in the same <c>ai_agent</c>
/// schema/Bounded Context.
///
/// Deliberately never persists: the raw guest message, the raw prompt, the
/// full conversation history, <c>GuestPhone</c>, the administrator's own
/// phone/destination (that lives exclusively in Communication's
/// <c>AdministratorNotificationContact</c> — CP6 mandate item 19/21), any
/// credential, QR, or provider payload. Only audit metadata: which session,
/// why, when requested, when notification was attempted/succeeded/failed,
/// when/by whom it was resumed.
///
/// Lifecycle: <see cref="Request"/> → <see cref="MarkNotified"/> →
/// <see cref="Resume"/>, or <see cref="Request"/> → <see cref="MarkNotificationFailed"/>
/// (leaves <see cref="Status"/> at <see cref="AgentHumanHandoffStatus.Requested"/>,
/// never a terminal failure state — CP6 mandate item 9: notification failure
/// never rolls back the handoff, never reactivates the AI, and may be
/// retried) → <see cref="Resume"/>. No <c>Assigned</c>/<c>Acknowledged</c>
/// value exists — NOT MVP (CP6 mandate item 42).
/// </summary>
public sealed class AgentHumanHandoff : AggregateRoot<Guid>, ITenantOwned
{
    public Guid TenantId { get; private set; }
    public Guid AgentSessionId { get; private set; }
    public AgentHumanHandoffReasonCode ReasonCode { get; private set; }
    public AgentHumanHandoffStatus Status { get; private set; }
    public DateTimeOffset RequestedAtUtc { get; private set; }
    public DateTimeOffset? NotificationAttemptedAtUtc { get; private set; }
    public DateTimeOffset? NotifiedAtUtc { get; private set; }
    public string? NotificationFailureCode { get; private set; }
    public DateTimeOffset? ResumedAtUtc { get; private set; }
    public Guid? ResumedByActorId { get; private set; }

    private AgentHumanHandoff()
    {
        // EF Core materialization.
    }

    private AgentHumanHandoff(Guid id, Guid tenantId, Guid agentSessionId, AgentHumanHandoffReasonCode reasonCode, DateTimeOffset now)
        : base(id)
    {
        TenantId = tenantId;
        AgentSessionId = agentSessionId;
        ReasonCode = reasonCode;
        Status = AgentHumanHandoffStatus.Requested;
        RequestedAtUtc = now;
    }

    public static AgentHumanHandoff Request(Guid id, Guid tenantId, Guid agentSessionId, AgentHumanHandoffReasonCode reasonCode, DateTimeOffset now)
    {
        if (agentSessionId == Guid.Empty)
            throw new ArgumentException("Agent session id cannot be empty.", nameof(agentSessionId));

        return new AgentHumanHandoff(id, tenantId, agentSessionId, reasonCode, now);
    }

    /// <summary>Records a notification attempt that failed — <see cref="Status"/> stays <see cref="AgentHumanHandoffStatus.Requested"/> (CP6 mandate item 9/29): never a rollback, never a reason to reactivate the session.</summary>
    public void MarkNotificationFailed(DateTimeOffset now, string failureCode)
    {
        if (Status != AgentHumanHandoffStatus.Requested)
            throw new InvalidOperationException($"Cannot record a notification failure for a handoff in status '{Status}'.");

        if (string.IsNullOrWhiteSpace(failureCode))
            throw new ArgumentException("Failure code cannot be empty.", nameof(failureCode));

        NotificationAttemptedAtUtc = now;
        NotificationFailureCode = failureCode;
    }

    /// <summary>Only reached when the real administrator notification succeeded (CP6 mandate item 9/29) — never optimistic.</summary>
    public void MarkNotified(DateTimeOffset now)
    {
        if (Status != AgentHumanHandoffStatus.Requested)
            throw new InvalidOperationException($"Cannot mark a handoff in status '{Status}' as notified.");

        Status = AgentHumanHandoffStatus.Notified;
        NotificationAttemptedAtUtc = now;
        NotifiedAtUtc = now;
        NotificationFailureCode = null;
    }

    /// <summary>Manual resume only (CP6 mandate item 22/35/41) — valid from either <see cref="AgentHumanHandoffStatus.Requested"/> (notification never succeeded) or <see cref="AgentHumanHandoffStatus.Notified"/>.</summary>
    public void Resume(DateTimeOffset now, Guid resumedByActorId)
    {
        if (Status == AgentHumanHandoffStatus.Resumed)
            throw new InvalidOperationException("This handoff has already been resumed.");

        if (resumedByActorId == Guid.Empty)
            throw new ArgumentException("Resumed-by actor id cannot be empty.", nameof(resumedByActorId));

        Status = AgentHumanHandoffStatus.Resumed;
        ResumedAtUtc = now;
        ResumedByActorId = resumedByActorId;
    }
}

/// <summary>No <c>Assigned</c>/<c>Acknowledged</c>/<c>InProgress</c>/<c>Closed</c> value exists (CP6 mandate item 9/42) — NOT MVP, no queue/assignment concept is built.</summary>
public enum AgentHumanHandoffStatus
{
    Requested,
    Notified,
    Resumed,
}

/// <summary>
/// Fixed, closed allowlist (CP6 mandate item 2/7) — the model classifies
/// <see cref="ModelResult.Intent"/> as a free provider-neutral string; the
/// backend alone maps it to one of these via a fixed, explicit table (never
/// dynamic/reflection, never a code the model supplies directly).
/// <c>InformationInsufficient</c> deliberately has no corresponding value —
/// Documento 16 §17/§28 already handles that case as "ask for clarification,"
/// never an automatic handoff.
/// </summary>
public enum AgentHumanHandoffReasonCode
{
    ExplicitHumanRequest,
    Refund,
    Accident,
    Police,
    Negotiation,
    SevereDamage,
    SeriousComplaint,
    AggressiveBehavior,
    LowConfidence,
    IntegrationFailure,
}
