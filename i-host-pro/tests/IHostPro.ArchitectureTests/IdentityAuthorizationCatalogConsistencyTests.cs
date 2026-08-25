using System.Reflection;
using FluentAssertions;
using IHostPro.Contexts.Identity.Api.Authorization;
using IHostPro.Contexts.Identity.Contracts.Authorization;
using IHostPro.Contexts.Identity.Infrastructure.Seed;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IHostPro.ArchitectureTests;

/// <summary>
/// Confirms every permission code <see cref="IdentityPermissionCodes"/>
/// exposes (and that <c>IdentityAuthorizationExtensions</c>'s policies are
/// therefore built from) actually exists in the seeded catalog
/// (<see cref="IdentityCatalogSeed.Permissions"/>) — the two are maintained
/// in different projects (Application vs. Infrastructure) precisely so
/// neither depends on ASP.NET Core, so nothing at compile time forces them to
/// stay in sync (Incremento 3 plan, Checkpoint 1 follow-up — approved
/// consistency fix). A policy requiring a permission code absent from the
/// catalog would deny every caller unconditionally, including an
/// Administrator — a silent, easy-to-miss defect this test guards against.
///
/// Checkpoint 2.3.2.2 correction: <see cref="RequiredAuthorizationPolicyNames"/>
/// replaces a hardcoded 5-entry list that was never extended as later
/// checkpoints added policies (Reservations, Policies, Cleanings, Schedule,
/// Dashboard, Templates, Integrations) — it never actually caught a missing
/// registration, including <c>INTEGRATIONS:MANAGE</c>, which shipped with a
/// real controller reference and a real catalog seed but no
/// <c>AddPolicy(...)</c> call, so every call to <c>WhatsAppIntegrationController</c>
/// 500'd for every caller. <see cref="Every_controller_required_policy_is_registered"/>
/// discovers every policy name any <c>[Authorize(Policy = ...)]</c> attribute
/// on a real controller requires, by reflecting over the same Api assemblies
/// already referenced here, and asserts each one resolves against the real
/// <see cref="IdentityAuthorizationExtensions.AddIdentityAuthorization"/>
/// registration — so a future controller referencing an unregistered policy
/// fails this test immediately, without needing its name added anywhere by
/// hand.
///
/// Known, deliberate gap: this only sees declarative
/// <c>[Authorize(Policy = ...)]</c> attributes. <c>ScheduleController</c>/
/// <c>DashboardController</c> instead call
/// <c>IAuthorizationService.AuthorizeAsync(User, policyCode)</c> imperatively
/// (an OR of two policies — a bare class-level <c>[Authorize]</c> plus two
/// stacked attributes would AND them instead), which reflection over
/// attributes cannot see. Both codes involved
/// (<see cref="IdentityPermissionCodes.ScheduleManage"/>/<see cref="IdentityPermissionCodes.ScheduleRead"/>/
/// <see cref="IdentityPermissionCodes.DashboardManage"/>/<see cref="IdentityPermissionCodes.DashboardRead"/>)
/// are already registered and already covered by real HTTP endpoint tests
/// elsewhere (<c>ScheduleEndpointsTests</c>/<c>DashboardOverviewEndpointsTests</c>),
/// so this is a coverage gap in this one architecture test specifically, not
/// an unguarded registration gap in the product.
/// </summary>
public class IdentityAuthorizationCatalogConsistencyTests
{
    // One representative type per Api assembly that declares controllers —
    // deliberately not a generic "scan every loaded assembly" sweep (that
    // would silently start reflecting over test assemblies, third-party
    // packages, etc.), and deliberately not a new shared "list of Api
    // assemblies" helper (no second consumer exists yet — Engineering
    // Constitution §7/§17).
    private static readonly Assembly[] ControllerAssemblies =
    [
        typeof(IdentityAuthorizationExtensions).Assembly,
        typeof(IHostPro.Contexts.PropertyManagement.Api.Controllers.PropertiesController).Assembly,
        typeof(IHostPro.Contexts.Reservations.Api.Controllers.ReservationsController).Assembly,
        typeof(IHostPro.Contexts.Configuration.Api.Controllers.PoliciesController).Assembly,
        typeof(IHostPro.Contexts.Housekeeping.Api.Controllers.CleaningsController).Assembly,
        typeof(IHostPro.Contexts.Dashboard.Api.Controllers.DashboardController).Assembly,
        typeof(IHostPro.Contexts.ExternalIntegrations.Api.Controllers.WhatsAppIntegrationController).Assembly,
    ];

    public static IEnumerable<object[]> RequiredAuthorizationPolicyNames() => ControllerAssemblies
        .SelectMany(assembly => assembly.GetTypes())
        .Where(type => typeof(ControllerBase).IsAssignableFrom(type))
        .SelectMany(controller => controller.GetCustomAttributes<AuthorizeAttribute>(inherit: true)
            .Concat(controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .SelectMany(method => method.GetCustomAttributes<AuthorizeAttribute>(inherit: true))))
        .Select(attribute => attribute.Policy)
        .Where(policy => !string.IsNullOrEmpty(policy))
        .Distinct()
        .OrderBy(policy => policy, StringComparer.Ordinal)
        .Select(policy => new object[] { policy! });

    [Theory]
    [MemberData(nameof(RequiredAuthorizationPolicyNames))]
    public async Task Every_controller_required_policy_is_registered(string policyName)
    {
        var services = new ServiceCollection();
        services.AddIdentityAuthorization();
        await using var provider = services.BuildServiceProvider();

        var policyProvider = provider.GetRequiredService<IAuthorizationPolicyProvider>();
        var policy = await policyProvider.GetPolicyAsync(policyName);

        policy.Should().NotBeNull(
            $"a controller requires policy \"{policyName}\" via [Authorize(Policy = ...)], " +
            $"so {nameof(IdentityAuthorizationExtensions)}.{nameof(IdentityAuthorizationExtensions.AddIdentityAuthorization)}() must register it");
    }

    public static IEnumerable<object[]> PolicyPermissionCodes() =>
        [
            [IdentityPermissionCodes.UsersManage],
            [IdentityPermissionCodes.RolesRead],
            [IdentityPermissionCodes.PermissionsRead],
            [IdentityPermissionCodes.PropertiesManage],
            [IdentityPermissionCodes.PropertiesReadOwnOwner],
        ];

    [Theory]
    [MemberData(nameof(PolicyPermissionCodes))]
    public void Every_policy_permission_code_exists_in_the_seeded_catalog(string permissionCode)
    {
        IdentityCatalogSeed.Permissions.Should().Contain(
            p => p.Id == permissionCode,
            $"a policy requires \"{permissionCode}\", so it must be a seeded, real permission code");
    }

    [Fact]
    public void ADMIN_role_is_granted_every_policy_permission_code()
    {
        // Not exhaustive proof of the role/permission matrix (Documento 09
        // §15) — just a guard that the specific codes this increment's
        // policies use are actually reachable by at least the Administrator,
        // so the endpoints they protect are not unconditionally inaccessible
        // once Checkpoint 2 adds the real handler.
        var adminPermissionCodes = IdentityCatalogSeed.RolePermissions
            .Where(rp => rp.RoleCode == "ADMIN")
            .Select(rp => rp.PermissionCode)
            .ToHashSet();

        adminPermissionCodes.Should().Contain(IdentityPermissionCodes.UsersManage);
        adminPermissionCodes.Should().Contain(IdentityPermissionCodes.RolesRead);
        adminPermissionCodes.Should().Contain(IdentityPermissionCodes.PermissionsRead);
        adminPermissionCodes.Should().Contain(IdentityPermissionCodes.PropertiesManage);
    }

    [Fact]
    public void PROPERTY_OWNER_role_is_granted_the_own_owner_read_policy_permission_code()
    {
        // Mirrors ADMIN_role_is_granted_every_policy_permission_code above,
        // for the one policy code Property Management's "mine" endpoints
        // require (Fase 2, Incremento 1, Checkpoint 5).
        var propertyOwnerPermissionCodes = IdentityCatalogSeed.RolePermissions
            .Where(rp => rp.RoleCode == IdentityRoleCodes.PropertyOwner)
            .Select(rp => rp.PermissionCode)
            .ToHashSet();

        propertyOwnerPermissionCodes.Should().Contain(IdentityPermissionCodes.PropertiesReadOwnOwner);
    }

    [Fact]
    public void IdentityPermissionCodes_has_exactly_one_canonical_source()
    {
        // Fase 2, Incremento 1, Checkpoint 1, approved design: the type moved
        // from Identity.Application to Identity.Contracts specifically so
        // Property Management could reference it without depending on
        // Identity.Application/Infrastructure. This guards against a second
        // "IdentityPermissionCodes" class ever being reintroduced anywhere in
        // the solution (e.g. a future contributor recreating it in
        // Application by mistake), which would silently fork the source of
        // truth this whole class exists to prevent.
        var assembliesToScan = new[]
        {
            typeof(IdentityPermissionCodes).Assembly, // Identity.Contracts
            typeof(IdentityCatalogSeed).Assembly, // Identity.Infrastructure
        };

        var matchingTypes = assembliesToScan
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => type.Name == nameof(IdentityPermissionCodes))
            .ToList();

        matchingTypes.Should().ContainSingle(
            "IdentityPermissionCodes must have exactly one canonical source, in Identity.Contracts");
        matchingTypes.Single().Namespace.Should().Be("IHostPro.Contexts.Identity.Contracts.Authorization");
    }

    [Fact]
    public void IdentityRoleCodes_has_exactly_one_canonical_source()
    {
        // Checkpoint 5 plan, item 4: "adicionar teste garantindo fonte única
        // para PROPERTY_OWNER" — mirrors IdentityPermissionCodes_has_exactly_one_canonical_source
        // exactly, for the analogous role-code constants class.
        var assembliesToScan = new[]
        {
            typeof(IdentityRoleCodes).Assembly, // Identity.Contracts
            typeof(IdentityCatalogSeed).Assembly, // Identity.Infrastructure
        };

        var matchingTypes = assembliesToScan
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => type.Name == nameof(IdentityRoleCodes))
            .ToList();

        matchingTypes.Should().ContainSingle(
            "IdentityRoleCodes must have exactly one canonical source, in Identity.Contracts");
        matchingTypes.Single().Namespace.Should().Be("IHostPro.Contexts.Identity.Contracts.Authorization");
    }
}
