using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.Identity.Domain;
using Microsoft.EntityFrameworkCore;

namespace IHostPro.Contexts.Identity.Infrastructure.Persistence;

/// <inheritdoc cref="IRepository{TAggregate,TId}"/>
/// <remarks>
/// Incremento 3, Checkpoint 5 — the first write path that needs to ADD a new
/// <see cref="User"/> from Application. Distinct from
/// <c>UserStore</c>/<c>UserManager&lt;User&gt;</c> (Login/Refresh's read path,
/// via <c>IUserAuthenticationService</c>) — both operate on the same
/// underlying <c>Users</c> table, exactly like <c>RefreshTokenReader</c> and
/// <c>RefreshTokenRepository</c> already coexist for <c>RefreshTokens</c>.
/// </remarks>
public sealed class UserRepository : IRepository<User, Guid>
{
    private readonly IdentityDbContext _dbContext;

    public UserRepository(IdentityDbContext dbContext) => _dbContext = dbContext;

    public async Task<User?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        await _dbContext.Users.FirstOrDefaultAsync(u => u.Id == id, cancellationToken);

    public void Add(User aggregate) => _dbContext.Users.Add(aggregate);

    public void Update(User aggregate) => _dbContext.Users.Update(aggregate);

    public void Remove(User aggregate) => _dbContext.Users.Remove(aggregate);
}
