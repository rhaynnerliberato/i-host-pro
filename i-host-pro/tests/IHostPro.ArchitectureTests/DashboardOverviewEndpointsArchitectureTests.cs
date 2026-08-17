using System.Reflection;
using FluentAssertions;
using IHostPro.Contexts.Dashboard.Api.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace IHostPro.ArchitectureTests;

/// <summary>
/// Guards <see cref="DashboardController"/>'s approved action set (Fase 7,
/// Incremento 2, Checkpoint 2) — mirrors <c>ReservationsEndpointsArchitectureTests</c>,
/// adjusted for the manual <see cref="Microsoft.AspNetCore.Authorization.IAuthorizationService"/>
/// OR-check pattern <c>ScheduleController</c>/<c>DashboardController</c> both
/// use (this codebase's <c>[Authorize(Policy=...)]</c> is always a single
/// exact code — never expressible as an OR of two).
/// </summary>
public class DashboardOverviewEndpointsArchitectureTests
{
    private static MethodInfo[] Actions() =>
        typeof(DashboardController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName)
            .ToArray();

    [Fact]
    public void Controller_exposes_exactly_the_one_approved_Overview_action()
    {
        var actionNames = Actions().Select(m => m.Name).ToArray();

        actionNames.Should().BeEquivalentTo(
        [
            nameof(DashboardController.Overview),
        ], "no per-card endpoint may exist this MVP (mandate §3) — a single aggregated Overview only");
    }

    [Fact]
    public void No_action_declares_AllowAnonymous_or_a_Policy_scoped_Authorize_attribute()
    {
        // Authorization is checked manually inside the action body (READ OR
        // MANAGE) — a Policy-scoped [Authorize] here would be a single exact
        // code, silently narrowing (or conflicting with) the manual OR-check.
        foreach (var action in Actions())
        {
            action.GetCustomAttributes<AllowAnonymousAttribute>().Should().BeEmpty(
                $"{action.Name} must never be reachable anonymously");

            action.GetCustomAttributes<AuthorizeAttribute>()
                .Should().NotContain(a => a.Policy != null,
                    $"{action.Name} must not declare a Policy-scoped [Authorize] — authorization is the manual READ-OR-MANAGE check in the action body");
        }
    }

    [Fact]
    public void No_action_declares_a_tenantId_or_actorId_parameter()
    {
        // The tenant/actor come exclusively from DashboardIdentityReader's
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
