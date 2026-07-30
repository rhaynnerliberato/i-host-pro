using IHostPro.Contexts.Identity.Application.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace IHostPro.Contexts.Identity.Api.Authorization;

/// <summary>
/// Registers the permission-code authorization policies this increment's
/// endpoints require, plus <see cref="PermissionAuthorizationHandler"/>, the
/// only handler that evaluates a <see cref="PermissionRequirement"/>
/// (Incremento 3 plan, Checkpoints 1-2). Called once from
/// <c>IHostPro.Api</c>'s composition root (<c>Program.cs</c>), mirroring how
/// every other Identity module registration is a single extension method
/// called from the Host — never from <c>Identity.Api</c> itself.
///
/// Policy names are the same <see cref="IdentityPermissionCodes"/> constants
/// <c>IdentityCatalogSeed</c> (Infrastructure) uses to seed the persisted
/// catalog — a single, framework-neutral source of truth, never a literal
/// duplicated here (Checkpoint 1 follow-up — approved consistency fix; see
/// <c>IdentityAuthorizationCatalogConsistencyTests</c> for the automated
/// check that the two stay in sync).
///
/// <see cref="PermissionAuthorizationHandler"/> is registered Scoped —
/// matching <c>IPermissionReader</c>/<c>IdentityDbContext</c>'s own lifetime
/// (Checkpoint 2, approved design) — as <see cref="IAuthorizationHandler"/>,
/// the shape <see cref="IAuthorizationService"/> resolves every registered
/// handler through.
///
/// Only the three policies actually consumed by this increment's endpoints
/// are registered here — not the full permission catalog speculatively
/// (Engineering Constitution §7/§17: no infrastructure without a concrete
/// current need). A future checkpoint that protects a new endpoint with a
/// permission code not yet listed here must add a policy for it at that
/// point.
/// </summary>
public static class IdentityAuthorizationExtensions
{
    public static IServiceCollection AddIdentityAuthorization(this IServiceCollection services)
    {
        services.AddAuthorizationBuilder()
            .AddPolicy(IdentityPermissionCodes.UsersManage, policy =>
                policy.Requirements.Add(new PermissionRequirement(IdentityPermissionCodes.UsersManage)))
            .AddPolicy(IdentityPermissionCodes.RolesRead, policy =>
                policy.Requirements.Add(new PermissionRequirement(IdentityPermissionCodes.RolesRead)))
            .AddPolicy(IdentityPermissionCodes.PermissionsRead, policy =>
                policy.Requirements.Add(new PermissionRequirement(IdentityPermissionCodes.PermissionsRead)));

        services.AddScoped<IAuthorizationHandler, PermissionAuthorizationHandler>();

        return services;
    }
}
