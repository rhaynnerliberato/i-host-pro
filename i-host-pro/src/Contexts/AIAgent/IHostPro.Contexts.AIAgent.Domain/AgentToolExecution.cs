using IHostPro.BuildingBlocks.Domain;

namespace IHostPro.Contexts.AIAgent.Domain;

/// <summary>
/// A single audited Read Tool invocation made by the model loop during an
/// <see cref="AgentInteraction"/> (Fase 11, Checkpoint 3 — Read Tools &amp;
/// Context Builder). References <see cref="AgentInteractionId"/> by id only —
/// mirrors <see cref="AgentInteraction"/>'s own opaque-id-reference style —
/// but, per this checkpoint's own mandate (item 8: "FK apenas dentro do
/// AIAgent BC"), the Infrastructure mapping backs it with a real database
/// foreign key to <c>agent_interactions</c>, since both tables live in the
/// same <c>ai_agent</c> schema/Bounded Context (never a cross-context FK).
///
/// Deliberately does not persist raw tool input/output, guest PII, any
/// credential/secret-reference, QR/payment payload, provider payload, or the
/// full model prompt — only the audit metadata this checkpoint's mandate
/// requires: which tool ran, when, how long, and how it ended.
///
/// <see cref="FailureCode"/> mirrors <c>PixChargeFailureReceived.FailureCode</c>'s
/// own established convention exactly: optional, short, provider/tool-neutral,
/// sanitized by the caller before being passed in — never a raw exception
/// message or stack trace.
///
/// Lifecycle is deliberately minimal (mandate item 6: "não criar máquina de
/// estados excessiva") — <c>Start</c> then exactly one of
/// <see cref="CompleteSuccessfully"/>/<see cref="CompleteWithFailure"/>, same
/// two-step shape as <see cref="AgentInteraction"/>'s own outcome lifecycle.
/// </summary>
public sealed class AgentToolExecution : AggregateRoot<Guid>, ITenantOwned
{
    public Guid TenantId { get; private set; }
    public Guid AgentInteractionId { get; private set; }
    public string ToolName { get; private set; } = null!;
    public DateTimeOffset StartedAtUtc { get; private set; }
    public DateTimeOffset? CompletedAtUtc { get; private set; }
    public AgentToolExecutionOutcome Outcome { get; private set; }
    public long? DurationMs { get; private set; }
    public string? FailureCode { get; private set; }

    private AgentToolExecution()
    {
        // EF Core materialization.
    }

    private AgentToolExecution(Guid id, Guid tenantId, Guid agentInteractionId, string toolName, DateTimeOffset startedAtUtc)
        : base(id)
    {
        TenantId = tenantId;
        AgentInteractionId = agentInteractionId;
        ToolName = toolName;
        StartedAtUtc = startedAtUtc;
        Outcome = AgentToolExecutionOutcome.InProgress;
    }

    public static AgentToolExecution Start(Guid id, Guid tenantId, Guid agentInteractionId, string toolName, DateTimeOffset startedAtUtc)
    {
        if (agentInteractionId == Guid.Empty)
            throw new ArgumentException("Agent interaction id cannot be empty.", nameof(agentInteractionId));

        if (string.IsNullOrWhiteSpace(toolName))
            throw new ArgumentException("Tool name cannot be empty.", nameof(toolName));

        return new AgentToolExecution(id, tenantId, agentInteractionId, toolName, startedAtUtc);
    }

    public void CompleteSuccessfully(DateTimeOffset completedAtUtc)
    {
        if (Outcome != AgentToolExecutionOutcome.InProgress)
            throw new InvalidOperationException($"Cannot complete a tool execution already in outcome '{Outcome}'.");

        CompletedAtUtc = completedAtUtc;
        DurationMs = (long)(completedAtUtc - StartedAtUtc).TotalMilliseconds;
        Outcome = AgentToolExecutionOutcome.Success;
    }

    public void CompleteWithFailure(DateTimeOffset completedAtUtc, string? failureCode)
    {
        if (Outcome != AgentToolExecutionOutcome.InProgress)
            throw new InvalidOperationException($"Cannot complete a tool execution already in outcome '{Outcome}'.");

        CompletedAtUtc = completedAtUtc;
        DurationMs = (long)(completedAtUtc - StartedAtUtc).TotalMilliseconds;
        FailureCode = failureCode;
        Outcome = AgentToolExecutionOutcome.Failure;
    }
}

/// <summary>Deliberately minimal, mirrors <see cref="AgentInteractionOutcome"/>'s own precedent — no partial/timeout/retry taxonomy exists yet.</summary>
public enum AgentToolExecutionOutcome
{
    InProgress,
    Success,
    Failure,
}
