namespace IHostPro.Contexts.Identity.Application;

/// <summary>
/// Reads a user's assigned role codes (Incremento 2 plan, Etapa 9). Separate
/// from ASP.NET Core Identity's <c>IUserRoleStore&lt;TUser&gt;</c>: RBAC in
/// this project is entirely the platform's own model (<c>Role</c>,
/// <c>UserRole</c>), never ASP.NET Core Identity's — <c>UserStore</c>
/// deliberately does not implement <c>IUserRoleStore&lt;User&gt;</c>
/// (Incremento 1 plan), so this is the abstraction Application-layer code
/// uses instead. An empty result is valid — a user with no assigned role is
/// not an error.
/// </summary>
public interface IUserRoleReader
{
    Task<IReadOnlyCollection<string>> GetRoleCodesAsync(Guid userId, CancellationToken cancellationToken);
}
