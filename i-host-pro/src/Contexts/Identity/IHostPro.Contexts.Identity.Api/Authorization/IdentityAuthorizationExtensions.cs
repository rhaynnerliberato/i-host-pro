using IHostPro.Contexts.Identity.Contracts.Authorization;
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
/// Only the policies actually consumed by an existing endpoint are
/// registered here — not the full permission catalog speculatively
/// (Engineering Constitution §7/§17: no infrastructure without a concrete
/// current need). A future checkpoint that protects a new endpoint with a
/// permission code not yet listed here must add a policy for it at that
/// point.
///
/// <see cref="IdentityPermissionCodes.PropertiesManage"/>/
/// <see cref="IdentityPermissionCodes.PropertiesReadOwnOwner"/> are
/// registered here (Fase 2, Incremento 1, Checkpoint 1, approved design) even
/// though no Property Management endpoint exists yet — this is the single,
/// central place ASP.NET Core authorization policies are composed for the
/// whole Host, precisely so Property Management's own module never needs to
/// register an <see cref="IAuthorizationHandler"/> or duplicate policy of its
/// own: its future controllers only ever reference
/// <c>IdentityPermissionCodes.PropertiesManage</c>/
/// <c>PropertiesReadOwnOwner</c> as a policy name string, resolved by
/// <see cref="PermissionAuthorizationHandler"/> below exactly like every
/// other permission code, since that handler is already fully generic — it
/// resolves any code present in the persisted catalog, not just Identity's
/// own resources.
///
/// <see cref="IdentityPermissionCodes.ReservationsManage"/> follows the
/// exact same pattern (Fase 3, Incremento 1 plan, item 5) — Reservations'
/// controller references it as a policy name string only.
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
                policy.Requirements.Add(new PermissionRequirement(IdentityPermissionCodes.PermissionsRead)))
            .AddPolicy(IdentityPermissionCodes.PropertiesManage, policy =>
                policy.Requirements.Add(new PermissionRequirement(IdentityPermissionCodes.PropertiesManage)))
            .AddPolicy(IdentityPermissionCodes.PropertiesReadOwnOwner, policy =>
                policy.Requirements.Add(new PermissionRequirement(IdentityPermissionCodes.PropertiesReadOwnOwner)))
            .AddPolicy(IdentityPermissionCodes.ReservationsManage, policy =>
                policy.Requirements.Add(new PermissionRequirement(IdentityPermissionCodes.ReservationsManage)));

        services.AddScoped<IAuthorizationHandler, PermissionAuthorizationHandler>();

        return services;
    }
}
