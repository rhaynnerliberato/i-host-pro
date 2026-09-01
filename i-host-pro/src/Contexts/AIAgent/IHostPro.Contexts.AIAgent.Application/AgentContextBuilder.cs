using IHostPro.Contexts.Communication.Contracts;

namespace IHostPro.Contexts.AIAgent.Application;

/// <inheritdoc cref="IAgentContextBuilder"/>
/// <remarks>
/// The only cross-context call this checkpoint's Context Builder makes:
/// <see cref="IConversationHistoryReader"/> (ADR-030, synchronous exception
/// #14). <see cref="ModelRequest.SystemPrompt"/> is left <see langword="null"/>
/// — no hardcoded business prompt exists (mandate item 16/18); a real
/// runtime prompt source is Configuration/Context Builder's future
/// responsibility (Checkpoint 3+), and <c>FakeModelProvider</c> does not need
/// one to function deterministically.
///
/// Fase 11, Checkpoint 4 — see <see cref="IAgentContextBuilder"/>'s own doc
/// comment for why <paramref name="triggeringInboundMessageId"/> exists: the
/// reader's own ordering can rarely tie between two messages created
/// microseconds apart, so this method re-sorts the fetched history in
/// memory to guarantee the triggering message is always last, rather than
/// trusting the reader's own tie-break for this specific, behaviorally
/// significant position.
/// </remarks>
public sealed class AgentContextBuilder : IAgentContextBuilder
{
    private readonly IConversationHistoryReader _historyReader;

    public AgentContextBuilder(IConversationHistoryReader historyReader) => _historyReader = historyReader;

    public async Task<ModelRequest> BuildAsync(
        Guid tenantId, Guid conversationId, Guid triggeringInboundMessageId, CancellationToken cancellationToken)
    {
        var history = await _historyReader.GetHistoryAsync(tenantId, conversationId, cancellationToken);

        var orderedHistory = history
            .OrderBy(m => m.MessageId == triggeringInboundMessageId ? 1 : 0)
            .ToList();

        var messages = orderedHistory
            .Select(m => new ModelMessage(
                m.Direction == ConversationMessageDirection.Inbound ? ModelMessageRole.Guest : ModelMessageRole.Agent,
                m.Content))
            .ToList();

        return new ModelRequest(SystemPrompt: null, Messages: messages);
    }
}
