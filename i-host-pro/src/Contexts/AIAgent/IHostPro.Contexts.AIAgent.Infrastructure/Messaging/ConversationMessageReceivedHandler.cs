using IHostPro.Contexts.AIAgent.Application;
using IHostPro.Contexts.Communication.Contracts;
using Wolverine.Attributes;
using Wolverine.Runtime;

namespace IHostPro.Contexts.AIAgent.Infrastructure.Messaging;

/// <summary>
/// Wolverine adapter for <c>ConversationMessageReceived</c> (Fase 11,
/// Checkpoint 2 — AI Agent Foundation). Depends ONLY on
/// <see cref="IAIAgentMessageExecutionScope"/> and Wolverine's own
/// <see cref="MessageContext"/> — never on <c>AIAgentDbContext</c> or the
/// message processor directly. Mirrors Communication's
/// <c>InboundGuestMessageReceivedHandler</c> exactly.
///
/// No <c>AddStickyHandler</c> binding is needed (ADR-020) — AI Agent is the
/// sole in-process consumer of <c>ConversationMessageReceived</c>, so no
/// fan-out/handler-chain-combining risk exists (mirrors every other
/// single-consumer queue in this platform, e.g.
/// "reservations.airbnb-import"). Message ORDER within a Conversation
/// (mandate item 37) is preserved by RabbitMQ's own FIFO delivery on this
/// dedicated queue combined with Wolverine's default sequential (non-parallel)
/// listener — no additional partition/grouping key mechanism exists in this
/// Wolverine version to invent one for.
/// </summary>
[NonTransactional]
public static class ConversationMessageReceivedHandler
{
    public static Task Handle(
        ConversationMessageReceived message,
        MessageContext context,
        IAIAgentMessageExecutionScope executionScope,
        CancellationToken cancellationToken) =>
        executionScope.ExecuteAsync(message, message.TenantId, context.Envelope!.Id, cancellationToken);
}
