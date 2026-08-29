using IHostPro.Contexts.Communication.Domain;

namespace IHostPro.Contexts.Communication.Application;

/// <inheritdoc cref="IConversationResolver"/>
public sealed class ConversationResolver : IConversationResolver
{
    private readonly IConversationRepository _repository;
    private readonly ICommunicationTransactionExecutor _transactionExecutor;

    public ConversationResolver(IConversationRepository repository, ICommunicationTransactionExecutor transactionExecutor)
    {
        _repository = repository;
        _transactionExecutor = transactionExecutor;
    }

    public Task<Guid> GetOrCreateActiveConversationIdAsync(
        Guid tenantId, Guid reservationId, string channel, DateTimeOffset occurredAtUtc, CancellationToken cancellationToken) =>
        _transactionExecutor.ExecuteAsync(async () =>
        {
            var existing = await _repository.GetActiveByReservationAndChannelAsync(reservationId, channel, cancellationToken);
            if (existing is not null)
            {
                existing.RecordMessageAt(occurredAtUtc);
                _repository.Update(existing);
                return existing.Id;
            }

            var conversation = Conversation.Create(Guid.NewGuid(), tenantId, reservationId, channel, occurredAtUtc);
            _repository.Add(conversation);
            return conversation.Id;
        }, cancellationToken);
}
