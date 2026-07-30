using IHostPro.Contexts.Identity.Application;
using IHostPro.Contexts.Identity.Domain;

namespace IHostPro.Contexts.Identity.Infrastructure.Persistence;

/// <inheritdoc cref="IUserRoleWriter"/>
public sealed class UserRoleWriter : IUserRoleWriter
{
    private readonly IdentityDbContext _dbContext;

    public UserRoleWriter(IdentityDbContext dbContext) => _dbContext = dbContext;

    public void Assign(UserRole userRole) => _dbContext.UserRoles.Add(userRole);

    public void Remove(UserRole userRole) => _dbContext.UserRoles.Remove(userRole);
}
