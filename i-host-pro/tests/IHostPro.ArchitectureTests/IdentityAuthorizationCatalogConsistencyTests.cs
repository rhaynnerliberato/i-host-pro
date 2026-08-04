using FluentAssertions;
using IHostPro.Contexts.Identity.Contracts.Authorization;
using IHostPro.Contexts.Identity.Infrastructure.Seed;
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
/// </summary>
public class IdentityAuthorizationCatalogConsistencyTests
{
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
