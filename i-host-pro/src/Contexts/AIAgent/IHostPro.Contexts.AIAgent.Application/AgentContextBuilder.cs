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
/// </remarks>
public sealed class AgentContextBuilder : IAgentContextBuilder
{
    private readonly IConversationHistoryReader _historyReader;

    public AgentContextBuilder(IConversationHistoryReader historyReader) => _historyReader = historyReader;

    public async Task<ModelRequest> BuildAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken)
    {
        var history = await _historyReader.GetHistoryAsync(tenantId, conversationId, cancellationToken);

        var messages = history
            .Select(m => new ModelMessage(
                m.Direction == ConversationMessageDirection.Inbound ? ModelMessageRole.Guest : ModelMessageRole.Agent,
                m.Content))
            .ToList();

        return new ModelRequest(SystemPrompt: null, Messages: messages);
    }
}
