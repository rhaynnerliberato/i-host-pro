using IHostPro.Contexts.Identity.Application;
using IHostPro.Contexts.Identity.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IHostPro.Contexts.Identity.Infrastructure.Identity;

/// <inheritdoc cref="IUserRoleReader"/>
public sealed class UserRoleReader : IUserRoleReader
{
    private readonly IdentityDbContext _dbContext;

    public UserRoleReader(IdentityDbContext dbContext) => _dbContext = dbContext;

    public async Task<IReadOnlyCollection<string>> GetRoleCodesAsync(Guid userId, CancellationToken cancellationToken) =>
        // UserRole implements ITenantOwned — BaseDbContext's Global Query
        // Filter already scopes this to the current tenant; no explicit
        // tenant_id filter is needed here.
        await _dbContext.UserRoles
            .Where(ur => ur.UserId == userId)
            .Select(ur => ur.RoleCode)
            .ToListAsync(cancellationToken);
}
