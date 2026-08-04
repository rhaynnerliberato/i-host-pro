using FluentAssertions;
using IHostPro.Contexts.Identity.Api.Authorization;
using IHostPro.Contexts.Identity.Contracts.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace IHostPro.Contexts.Identity.Tests.Integration;

/// <summary>
/// Confirms <see cref="IdentityAuthorizationExtensions.AddIdentityAuthorization"/>
/// (Incremento 3 plan, Checkpoints 1-2; Fase 2 Incremento 1 Checkpoint 1
/// adds the two Property Management policies) registers a policy for each
/// permission code Identity's own endpoints or Property Management's future
/// ones need, each backed by a
/// <see cref="PermissionRequirement"/> carrying the matching permission code
/// from <see cref="IdentityPermissionCodes"/> — the single, framework-neutral
/// source of truth also used by <c>IdentityCatalogSeed</c> (Checkpoint 1
/// follow-up — approved consistency fix).
///
/// Pure DI-container check — no database, no HTTP server, no Testcontainers,
/// unlike its neighbors in this project. Kept here (rather than in
/// <c>IHostPro.Contexts.Identity.Tests.Unit</c>) because it exercises
/// <c>Identity.Api</c>, which only this project and
/// <c>IHostPro.ArchitectureTests</c> currently reference.
///
/// This class previously also asserted that every policy denied by default
/// because no handler existed yet — that was Checkpoint 1's deliberate,
/// temporary scope boundary, and the test was written to fail loudly the
/// moment it stopped being true. Checkpoint 2 registered
/// <see cref="PermissionAuthorizationHandler"/>, so that boundary is gone by
/// design: the test failed exactly as intended and was removed here, its
/// coverage now superseded by <c>PermissionAuthorizationHandlerTests</c>
/// (the handler's own logic) and <c>PermissionAuthorizationEndToEndTests</c>
/// (the full real pipeline, including an unauthenticated request).
/// </summary>
public class IdentityAuthorizationExtensionsTests
{
    [Theory]
    [InlineData(IdentityPermissionCodes.UsersManage)]
    [InlineData(IdentityPermissionCodes.RolesRead)]
    [InlineData(IdentityPermissionCodes.PermissionsRead)]
    [InlineData(IdentityPermissionCodes.PropertiesManage)]
    [InlineData(IdentityPermissionCodes.PropertiesReadOwnOwner)]
    public async Task AddIdentityAuthorization_registers_a_policy_requiring_the_matching_permission_code(
        string permissionCode)
    {
        var services = new ServiceCollection();
        services.AddIdentityAuthorization();
        await using var provider = services.BuildServiceProvider();

        var policyProvider = provider.GetRequiredService<IAuthorizationPolicyProvider>();
        var policy = await policyProvider.GetPolicyAsync(permissionCode);

        policy.Should().NotBeNull();
        policy!.Requirements.OfType<PermissionRequirement>()
            .Should().ContainSingle(r => r.PermissionCode == permissionCode);
    }
}

public class PermissionRequirementTests
{
    [Fact]
    public void Constructor_sets_PermissionCode()
    {
        var requirement = new PermissionRequirement(IdentityPermissionCodes.UsersManage);

        requirement.PermissionCode.Should().Be(IdentityPermissionCodes.UsersManage);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_rejects_empty_permission_code(string? permissionCode)
    {
        var act = () => new PermissionRequirement(permissionCode!);

        act.Should().Throw<ArgumentException>();
    }
}
