using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.Identity.Domain;
using Microsoft.EntityFrameworkCore;

namespace IHostPro.Contexts.Identity.Infrastructure.Persistence;

/// <inheritdoc cref="IRepository{TAggregate,TId}"/>
public sealed class RefreshTokenRepository : IRepository<RefreshToken, Guid>
{
    private readonly IdentityDbContext _dbContext;

    public RefreshTokenRepository(IdentityDbContext dbContext) => _dbContext = dbContext;

    public async Task<RefreshToken?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        await _dbContext.RefreshTokens.FirstOrDefaultAsync(rt => rt.Id == id, cancellationToken);

    public void Add(RefreshToken aggregate) => _dbContext.RefreshTokens.Add(aggregate);

    public void Update(RefreshToken aggregate) => _dbContext.RefreshTokens.Update(aggregate);

    public void Remove(RefreshToken aggregate) => _dbContext.RefreshTokens.Remove(aggregate);
}
