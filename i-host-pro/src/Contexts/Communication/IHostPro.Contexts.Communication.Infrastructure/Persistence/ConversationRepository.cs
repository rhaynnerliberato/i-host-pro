using IHostPro.Contexts.Communication.Application;
using IHostPro.Contexts.Communication.Domain;
using Microsoft.EntityFrameworkCore;

namespace IHostPro.Contexts.Communication.Infrastructure.Persistence;

public sealed class ConversationRepository : IConversationRepository
{
    private readonly CommunicationDbContext _dbContext;

    public ConversationRepository(CommunicationDbContext dbContext) => _dbContext = dbContext;

    public Task<Conversation?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        _dbContext.Conversations.FirstOrDefaultAsync(c => c.Id == id, cancellationToken);

    public Task<Conversation?> GetActiveByReservationAndChannelAsync(
        Guid reservationId, string channel, CancellationToken cancellationToken) =>
        _dbContext.Conversations.FirstOrDefaultAsync(
            c => c.ReservationId == reservationId && c.Channel == channel && c.Status == ConversationStatus.Active,
            cancellationToken);

    public void Add(Conversation aggregate) => _dbContext.Conversations.Add(aggregate);

    public void Update(Conversation aggregate) => _dbContext.Conversations.Update(aggregate);

    public void Remove(Conversation aggregate) => _dbContext.Conversations.Remove(aggregate);
}
