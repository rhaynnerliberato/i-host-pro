using FluentAssertions;
using IHostPro.Contexts.Identity.Application.Authorization;
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
        // §15) — just a guard that the specific three codes this increment's
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
    }
}
