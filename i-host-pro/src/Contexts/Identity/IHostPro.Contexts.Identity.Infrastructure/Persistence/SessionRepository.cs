using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.Identity.Domain;
using Microsoft.EntityFrameworkCore;

namespace IHostPro.Contexts.Identity.Infrastructure.Persistence;

/// <inheritdoc cref="IRepository{TAggregate,TId}"/>
public sealed class SessionRepository : IRepository<Session, Guid>
{
    private readonly IdentityDbContext _dbContext;

    public SessionRepository(IdentityDbContext dbContext) => _dbContext = dbContext;

    public async Task<Session?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        await _dbContext.Sessions.FirstOrDefaultAsync(s => s.Id == id, cancellationToken);

    public void Add(Session aggregate) => _dbContext.Sessions.Add(aggregate);

    public void Update(Session aggregate) => _dbContext.Sessions.Update(aggregate);

    public void Remove(Session aggregate) => _dbContext.Sessions.Remove(aggregate);
}
