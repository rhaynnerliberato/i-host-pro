using System.Reflection;
using FluentAssertions;
using IHostPro.Contexts.Identity.Contracts.Authorization;
using IHostPro.Contexts.PropertyManagement.Api.Contracts;
using IHostPro.Contexts.PropertyManagement.Api.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace IHostPro.ArchitectureTests;

/// <summary>
/// Guards the ten administrative Property endpoints — four CRUD (Checkpoint
/// 3), three lifecycle transitions (Checkpoint 4), three Ownership
/// administration actions (Checkpoint 5) — <see cref="PropertiesController"/>
/// — mirroring <see cref="PropertyManagementCondominiumsEndpointsArchitectureTests"/>'s
/// per-action policy check and exact-action-set check. The controller-set-wide
/// check (no Group/Portaria controller exists yet) lives in that same class,
/// since it spans the whole assembly rather than this controller alone. See
/// <see cref="PropertyManagementMyPropertiesEndpointsArchitectureTests"/>
/// for the self-service <c>mine</c> controller's own checks.
/// </summary>
public class PropertyManagementPropertiesEndpointsArchitectureTests
{
    private static MethodInfo[] Actions() =>
        typeof(PropertiesController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName)
            .ToArray();

    [Fact]
    public void Controller_exposes_exactly_the_ten_approved_actions()
    {
        var actionNames = Actions().Select(m => m.Name).ToArray();

        actionNames.Should().BeEquivalentTo([
            nameof(PropertiesController.Create),
            nameof(PropertiesController.List),
            nameof(PropertiesController.GetById),
            nameof(PropertiesController.Update),
            nameof(PropertiesController.Activate),
            nameof(PropertiesController.Deactivate),
            nameof(PropertiesController.Archive),
            nameof(PropertiesController.LinkOwner),
            nameof(PropertiesController.UnlinkOwner),
            nameof(PropertiesController.ListOwners),
        ], "every administrative action must be explicitly approved before it exists here — no Group or " +
           "Portaria endpoint may exist yet (Checkpoint 5 plan, restrictions)");
    }

    [Fact]
    public void Every_action_requires_exactly_the_PROPERTIES_MANAGE_policy()
    {
        foreach (var action in Actions())
        {
            var authorizeAttributes = action.GetCustomAttributes<AuthorizeAttribute>().ToArray();

            authorizeAttributes.Should().ContainSingle(
                a => a.Policy == IdentityPermissionCodes.PropertiesManage,
                $"{action.Name} must be protected by exactly the \"{IdentityPermissionCodes.PropertiesManage}\" policy, " +
                "never left open, bound to a different policy, or reachable by plain authentication alone");
            action.GetCustomAttributes<AllowAnonymousAttribute>().Should().BeEmpty(
                $"{action.Name} must never be reachable anonymously");
        }
    }

    [Fact]
    public void No_request_contract_declares_a_forbidden_client_supplied_field()
    {
        // Checkpoint 3 plan, item 2: request bodies must never accept
        // tenantId/actorId/status/createdBy/updatedBy/ownerUserId/role/permission
        // — the actor/tenant come exclusively from PropertyManagementIdentityReader,
        // and every Property is born in Draft.
        string[] forbiddenFragments =
        [
            "tenantid", "actorid", "createdby", "updatedby", "ownerid", "owneruserid", "claims", "role", "permission", "status",
        ];
        Type[] requestTypes = [typeof(CreatePropertyRequest), typeof(UpdatePropertyRequest), typeof(AddressRequest)];

        foreach (var requestType in requestTypes)
        {
            var propertyNames = requestType.GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name);

            propertyNames.Should().NotContain(
                name => forbiddenFragments.Any(fragment => name.Contains(fragment, StringComparison.OrdinalIgnoreCase)),
                $"{requestType.Name} must never declare a tenant/actor/status/claims/role/permission field");
        }
    }

    [Fact]
    public void PropertySummaryResponse_never_carries_own_or_effective_address()
    {
        // Checkpoint 3 plan, item 9: "listagem nunca retorna endereço próprio ou efetivo."
        var propertyNames = typeof(PropertySummaryResponse)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name);

        propertyNames.Should().NotContain(
            name => name.Contains("address", StringComparison.OrdinalIgnoreCase),
            $"{nameof(PropertySummaryResponse)} must never carry any address");
    }

    [Fact]
    public void PropertyDetailResponse_exposes_both_the_own_and_the_effective_address_with_its_source()
    {
        // Checkpoint 3 plan, item 8: the detail shape must let clients tell
        // apart the property's own address from the resolved effective one.
        var propertyNames = typeof(PropertyDetailResponse)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToArray();

        propertyNames.Should().Contain(nameof(PropertyDetailResponse.Address));
        propertyNames.Should().Contain(nameof(PropertyDetailResponse.EffectiveAddress));
        propertyNames.Should().Contain(nameof(PropertyDetailResponse.EffectiveAddressSource));
    }

    [Theory]
    [InlineData(nameof(PropertiesController.Activate))]
    [InlineData(nameof(PropertiesController.Deactivate))]
    [InlineData(nameof(PropertiesController.Archive))]
    public void Lifecycle_actions_accept_no_body(string actionName)
    {
        // Checkpoint 4 plan, item 1: "Não aceitar body nesses endpoints." —
        // the only parameters allowed are the route-bound propertyId and the
        // ambient CancellationToken; no [FromBody] request DTO parameter.
        var action = Actions().Single(m => m.Name == actionName);
        var parameters = action.GetParameters();

        parameters.Should().HaveCount(2, $"{actionName} must accept only the route id and a CancellationToken — no request body");
        parameters.Should().Contain(p => p.ParameterType == typeof(Guid) && p.Name == "propertyId");
        parameters.Should().Contain(p => p.ParameterType == typeof(CancellationToken));
    }

    [Fact]
    public void UnlinkOwner_accepts_no_body()
    {
        // DELETE /api/v1/properties/{propertyId}/owners/{ownerUserId} —
        // both ids are route-bound (Checkpoint 5 plan, item 8).
        var action = Actions().Single(m => m.Name == nameof(PropertiesController.UnlinkOwner));
        var parameters = action.GetParameters();

        parameters.Should().HaveCount(3);
        parameters.Should().Contain(p => p.ParameterType == typeof(Guid) && p.Name == "propertyId");
        parameters.Should().Contain(p => p.ParameterType == typeof(Guid) && p.Name == "ownerUserId");
        parameters.Should().Contain(p => p.ParameterType == typeof(CancellationToken));
    }

    [Fact]
    public void LinkPropertyOwnerRequest_declares_only_OwnerUserId()
    {
        // Checkpoint 5 plan, item 7: "Não aceitar: tenantId; actorId;
        // requiredRoleCode; role; isActive; createdBy." — OwnerUserId is the
        // ONE legitimate client-supplied field this request carries
        // (deliberately not covered by the generic forbidden-fragment check
        // above, which would otherwise flag OwnerUserId's own presence here).
        var propertyNames = typeof(LinkPropertyOwnerRequest)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToArray();

        propertyNames.Should().BeEquivalentTo([nameof(LinkPropertyOwnerRequest.OwnerUserId)]);
    }

    [Fact]
    public void PropertyOwnerResponse_never_carries_owner_personal_data()
    {
        // Checkpoint 5 plan, item 9: never name/email/status/role.
        string[] forbiddenFragments = ["name", "email", "status", "role"];
        var propertyNames = typeof(PropertyOwnerResponse)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToArray();

        propertyNames.Should().NotContain(
            name => forbiddenFragments.Any(fragment => name.Contains(fragment, StringComparison.OrdinalIgnoreCase)));
    }
}
