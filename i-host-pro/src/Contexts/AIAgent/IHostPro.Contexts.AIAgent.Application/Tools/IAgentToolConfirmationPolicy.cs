namespace IHostPro.Contexts.AIAgent.Application.Tools;

/// <summary>
/// Decides which write Tools require the guest's own conversational
/// confirmation before executing (Fase 11, Checkpoint 4 — translates CP0's
/// <c>WriteConfirmation=REQUIRED</c> decision into a technical model). The
/// model itself never decides this (CP4 mandate item 9/18 — no
/// <c>RequiresConfirmation</c> field exists on <c>ModelToolCallRequest</c>);
/// this is a fixed, server-side allowlist the orchestrator consults after
/// receiving a <c>ToolCallRequest</c>, never something a forged/malicious
/// tool-call payload could influence.
/// </summary>
public interface IAgentToolConfirmationPolicy
{
    bool RequiresConfirmation(string toolName);
}

/// <summary>
/// Fixed mapping (CP4 mandate item 9 — "não generalizar excessivamente"):
/// <c>RequestEarlyCheckIn</c>/<c>RequestLateCheckout</c> require
/// confirmation; <c>RequestGuestAccessDelivery</c> does not (the guest's own
/// explicit request already is the confirmation, CP0 decision); every Read
/// Tool from Checkpoint 3 never reaches this check at all (the orchestrator
/// only consults this policy for write Tools).
/// </summary>
public sealed class AgentToolConfirmationPolicy : IAgentToolConfirmationPolicy
{
    private static readonly IReadOnlySet<string> ConfirmationRequiredToolNames = new HashSet<string>(StringComparer.Ordinal)
    {
        AgentToolNames.RequestEarlyCheckIn,
        AgentToolNames.RequestLateCheckout,
    };

    public bool RequiresConfirmation(string toolName) => ConfirmationRequiredToolNames.Contains(toolName);
}
