namespace IHostPro.Contexts.AIAgent.Application;

/// <summary>
/// Delivers the AI Agent's own final response text as a real outbound
/// message (Fase 11, Checkpoint 4). The only abstraction
/// <see cref="ConversationMessageReceivedProcessor"/> depends on for this —
/// the concrete implementation (Infrastructure) is the Exception #3 adapter
/// that actually calls Communication's own <c>SendAgentResponseCommand</c>,
/// mirroring how <see cref="Tools.IAgentTool"/> keeps every cross-context
/// call out of this Application layer. <see cref="SendAgentResponseCommand"/>
/// is deliberately NOT a model-callable Tool — this interface is
/// orchestration infrastructure, never listed in <c>AvailableTools</c>.
/// </summary>
public interface IAgentResponseDeliveryService
{
    Task<AgentResponseDeliveryResult> SendAsync(
        Guid tenantId, Guid conversationId, Guid reservationId, Guid agentInteractionId, string content, CancellationToken cancellationToken);
}

/// <summary>
/// <see cref="MessageId"/> is set only when <see cref="IsSuccess"/> — the
/// caller records it as <c>AgentInteraction.OutboundMessageId</c>.
/// <see cref="FailureCode"/> is sanitized (CP4 mandate item 30) — never a
/// raw exception message.
/// </summary>
public sealed record AgentResponseDeliveryResult(bool IsSuccess, Guid? MessageId, string? FailureCode);
