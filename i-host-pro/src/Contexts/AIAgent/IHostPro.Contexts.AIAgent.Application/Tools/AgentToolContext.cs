namespace IHostPro.Contexts.AIAgent.Application.Tools;

/// <summary>
/// Backend-derived identifiers made available to every <see cref="IAgentTool"/>
/// execution (Fase 11, Checkpoint 3 — Read Tools &amp; Context Builder,
/// mandate item 3). Every field here is resolved by the orchestrator itself
/// from the triggering <c>ConversationMessageReceived</c>/session/interaction
/// — the model NEVER supplies any of these, and no tool argument may
/// duplicate one of them.
/// </summary>
public sealed record AgentToolContext(
    Guid TenantId,
    Guid ConversationId,
    Guid ReservationId,
    Guid AgentSessionId,
    Guid AgentInteractionId);
