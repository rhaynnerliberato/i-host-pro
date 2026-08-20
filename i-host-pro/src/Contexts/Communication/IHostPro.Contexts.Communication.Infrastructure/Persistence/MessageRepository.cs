using IHostPro.Contexts.Communication.Application;
using IHostPro.Contexts.Communication.Domain;
using Microsoft.EntityFrameworkCore;

namespace IHostPro.Contexts.Communication.Infrastructure.Persistence;

public sealed class MessageRepository : IMessageRepository
{
    private readonly CommunicationDbContext _dbContext;

    public MessageRepository(CommunicationDbContext dbContext) => _dbContext = dbContext;

    public Task<Message?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        _dbContext.Messages.FirstOrDefaultAsync(m => m.Id == id, cancellationToken);

    public Task<Message?> GetByIdempotencyKeyAsync(string idempotencyKey, CancellationToken cancellationToken) =>
        _dbContext.Messages.FirstOrDefaultAsync(m => m.IdempotencyKey == idempotencyKey, cancellationToken);

    public Task<Message?> GetByProviderMessageIdAsync(string providerMessageId, CancellationToken cancellationToken) =>
        _dbContext.Messages.SingleOrDefaultAsync(m => m.ProviderMessageId == providerMessageId, cancellationToken);

    public void Add(Message aggregate) => _dbContext.Messages.Add(aggregate);

    public void Update(Message aggregate) => _dbContext.Messages.Update(aggregate);

    public void Remove(Message aggregate) => _dbContext.Messages.Remove(aggregate);
}
