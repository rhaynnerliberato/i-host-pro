using FluentAssertions;
using Microsoft.Playwright;

namespace IHostPro.Web.Tests.E2E;

/// <summary>
/// Real-browser coverage for the "Políticas" nav item/route gate — Fase 5,
/// Incremento 1, Checkpoint 5. Unlike every other feature in this suite, the
/// seeded permission catalog (<c>IdentityCatalogSeed</c>) deliberately gives
/// no single role both <c>POLICIES:READ</c> and <c>POLICIES:MANAGE</c> (only
/// ADMIN has MANAGE, only AI_AGENT has READ) — the route/nav item are gated
/// on either one (OR semantics, <c>permissionGuard</c>'s own
/// <c>.some(...)</c>), which <see cref="AdminLayout"/>'s own unit tests
/// already cover in isolation. This suite proves the real, server-enforced
/// side: OPERATOR (neither code) must be denied exactly like
/// <see cref="PropertyManagementAuthorizationE2ETests"/> proves for
/// <c>PROPERTIES:MANAGE</c>, and ADMIN (holding MANAGE) must reach the route
/// for real.
///
/// <see cref="WebE2EFixtureCollection"/>: shares one <see cref="WebE2EFixture"/>
/// instance with the rest of this suite so xUnit never boots two fixtures in
/// parallel — both bind the same fixed RabbitMQ host port (5672).
/// </summary>
[Collection(WebE2EFixtureCollection.Name)]
public sealed class PoliciesAuthorizationE2ETests
{
    private readonly WebE2EFixture _fixture;

    public PoliciesAuthorizationE2ETests(WebE2EFixture fixture) => _fixture = fixture;

    private async Task<IPage> NewPageAsync()
    {
        var context = await _fixture.Browser.NewContextAsync();
        return await context.NewPageAsync();
    }

    private static async Task LoginAsync(IPage page, string tenantSlug, string email, string password)
    {
        await page.GetByLabel("Empresa").FillAsync(tenantSlug);
        await page.GetByLabel("E-mail").FillAsync(email);
        await page.GetByLabel("Senha").FillAsync(password);
        await page.GetByRole(AriaRole.Button, new() { Name = "Entrar" }).ClickAsync();
    }

    [Fact]
    public async Task An_authenticated_user_without_POLICIES_READ_or_POLICIES_MANAGE_cannot_view_or_access_Politicas()
    {
        var page = await NewPageAsync();
        await page.GotoAsync(_fixture.WebBaseUrl + "/login");
        await LoginAsync(page, WebE2EFixture.TenantSlugValue, WebE2EFixture.OperatorEmail, WebE2EFixture.OperatorPassword);
        await page.WaitForURLAsync(_fixture.WebBaseUrl + "/");

        await page.GetByText(WebE2EFixture.OperatorFullName).First.WaitForAsync(new LocatorWaitForOptions { Timeout = 15000 });
        var policiesNavItem = page.GetByRole(AriaRole.Link, new() { Name = "Políticas" });
        (await policiesNavItem.CountAsync()).Should().Be(0, "OPERATOR holds neither POLICIES:READ nor POLICIES:MANAGE, so the nav item must not render at all");

        await page.GotoAsync(_fixture.WebBaseUrl + "/policies");

        await page.WaitForURLAsync(_fixture.WebBaseUrl + "/forbidden");
        page.Url.Should().Be(_fixture.WebBaseUrl + "/forbidden", "the route guard must deny direct navigation by real permission, not just hide the nav link");
    }

    [Fact]
    public async Task An_ADMIN_user_sees_the_Politicas_nav_item_and_can_reach_the_route()
    {
        var page = await NewPageAsync();
        await page.GotoAsync(_fixture.WebBaseUrl + "/login");
        await LoginAsync(page, WebE2EFixture.TenantSlugValue, WebE2EFixture.AdminEmail, WebE2EFixture.AdminPassword);
        await page.WaitForURLAsync(_fixture.WebBaseUrl + "/");

        await page.GetByRole(AriaRole.Link, new() { Name = "Políticas" }).ClickAsync();
        await page.WaitForURLAsync(_fixture.WebBaseUrl + "/policies");

        await page.GetByRole(AriaRole.Heading, new() { Name = "Políticas" }).WaitForAsync();
    }
}
