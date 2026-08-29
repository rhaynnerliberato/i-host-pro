using IHostPro.Contexts.AIAgent.Domain;

namespace IHostPro.Contexts.AIAgent.Application;

/// <inheritdoc cref="IAgentSessionResolver"/>
public sealed class AgentSessionResolver : IAgentSessionResolver
{
    private readonly IAgentSessionRepository _repository;
    private readonly IAIAgentTransactionExecutor _transactionExecutor;

    public AgentSessionResolver(IAgentSessionRepository repository, IAIAgentTransactionExecutor transactionExecutor)
    {
        _repository = repository;
        _transactionExecutor = transactionExecutor;
    }

    public Task<Guid> GetOrCreateActiveSessionIdAsync(
        Guid tenantId, Guid conversationId, Guid reservationId, DateTimeOffset occurredAtUtc, CancellationToken cancellationToken) =>
        _transactionExecutor.ExecuteAsync(async () =>
        {
            var existing = await _repository.GetActiveByConversationIdAsync(conversationId, cancellationToken);
            if (existing is not null)
                return existing.Id;

            var session = AgentSession.Create(Guid.NewGuid(), tenantId, conversationId, reservationId, occurredAtUtc);
            _repository.Add(session);
            return session.Id;
        }, cancellationToken);
}
