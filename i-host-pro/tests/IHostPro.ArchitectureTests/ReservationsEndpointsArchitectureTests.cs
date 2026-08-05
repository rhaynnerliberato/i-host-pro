using System.Reflection;
using FluentAssertions;
using IHostPro.Contexts.Identity.Contracts.Authorization;
using IHostPro.Contexts.Reservations.Api.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace IHostPro.ArchitectureTests;

/// <summary>
/// Guards <see cref="ReservationsController"/>'s approved action set (Fase
/// 3, Incremento 1 plan) — mirrors <c>PropertyManagementPropertiesEndpointsArchitectureTests</c>.
/// </summary>
public class ReservationsEndpointsArchitectureTests
{
    private static MethodInfo[] Actions() =>
        typeof(ReservationsController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName)
            .ToArray();

    [Fact]
    public void Controller_exposes_exactly_the_five_approved_actions()
    {
        var actionNames = Actions().Select(m => m.Name).ToArray();

        actionNames.Should().BeEquivalentTo([
            nameof(ReservationsController.Create),
            nameof(ReservationsController.List),
            nameof(ReservationsController.GetById),
            nameof(ReservationsController.Update),
            nameof(ReservationsController.Cancel),
        ], "no physical/logical exclusion action may exist this increment");
    }

    [Fact]
    public void Every_action_requires_exactly_the_RESERVATIONS_MANAGE_policy()
    {
        foreach (var action in Actions())
        {
            var authorizeAttributes = action.GetCustomAttributes<AuthorizeAttribute>().ToArray();

            authorizeAttributes.Should().ContainSingle(
                a => a.Policy == IdentityPermissionCodes.ReservationsManage,
                $"{action.Name} must be protected by exactly the \"{IdentityPermissionCodes.ReservationsManage}\" policy");
            action.GetCustomAttributes<AllowAnonymousAttribute>().Should().BeEmpty(
                $"{action.Name} must never be reachable anonymously");
        }
    }

    [Fact]
    public void Cancel_accepts_no_body()
    {
        var cancel = typeof(ReservationsController).GetMethod(nameof(ReservationsController.Cancel))!;

        cancel.GetParameters().Should().NotContain(
            p => p.GetCustomAttribute<FromBodyAttribute>() != null,
            "Cancel is a pure state transition with no client-supplied body");
    }

    [Fact]
    public void No_action_declares_a_tenantId_or_actorId_parameter()
    {
        // The tenant/actor come exclusively from ReservationsIdentityReader's
        // validated claims — never from the client.
        foreach (var action in Actions())
        {
            var parameterNames = action.GetParameters().Select(p => p.Name).ToArray();

            parameterNames.Should().NotContain(
                name => name!.Contains("tenantId", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("actorId", StringComparison.OrdinalIgnoreCase),
                $"{action.Name} must never accept tenant/actor id from the client");
        }
    }
}
