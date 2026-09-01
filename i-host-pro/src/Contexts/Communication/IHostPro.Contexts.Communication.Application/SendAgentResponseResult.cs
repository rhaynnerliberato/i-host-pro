namespace IHostPro.Contexts.Communication.Application;

/// <summary>
/// Fase 11, Checkpoint 4. <see cref="MessageId"/> is returned synchronously
/// so the caller (AI Agent) can record it as <c>AgentInteraction.OutboundMessageId</c>
/// without a new Integration Event existing solely for that purpose (CP4
/// mandate item 32).
/// </summary>
public sealed record SendAgentResponseResult(Guid MessageId);
