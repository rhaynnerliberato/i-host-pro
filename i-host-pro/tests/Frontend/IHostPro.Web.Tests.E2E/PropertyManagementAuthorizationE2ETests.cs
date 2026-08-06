using FluentAssertions;
using Microsoft.Playwright;

namespace IHostPro.Web.Tests.E2E;

/// <summary>
/// Real-browser coverage for the authorization gate required before the rest
/// of Fase 4 Incremento 3 (Gestão de Condomínios e Imóveis) could proceed: the
/// "Condomínios"/"Imóveis" nav items and the <c>/condominiums</c>/<c>/properties</c>
/// routes must be gated on the user's real effective permissions
/// (<c>PROPERTIES:MANAGE</c>, resolved server-side via <c>IPermissionReader</c>
/// from <c>GET /api/v1/users/me</c>) — never on a role name, never on merely
/// being authenticated. Mirrors <see cref="UsersAuthorizationE2ETests"/>'s
/// pattern exactly, reusing the same seeded OPERATOR (never granted
/// <c>PROPERTIES:MANAGE</c>, per <c>IdentityCatalogSeed</c>).
///
/// <see cref="WebE2EFixtureCollection"/>: shares one <see cref="WebE2EFixture"/>
/// instance with the rest of this suite so xUnit never boots two fixtures in
/// parallel — both bind the same fixed RabbitMQ host port (5672).
/// </summary>
[Collection(WebE2EFixtureCollection.Name)]
public sealed class PropertyManagementAuthorizationE2ETests
{
    private readonly WebE2EFixture _fixture;

    public PropertyManagementAuthorizationE2ETests(WebE2EFixture fixture) => _fixture = fixture;

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
    public async Task An_authenticated_user_without_PROPERTIES_MANAGE_cannot_view_or_access_Condominios()
    {
        var page = await NewPageAsync();
        await page.GotoAsync(_fixture.WebBaseUrl + "/login");
        await LoginAsync(page, WebE2EFixture.TenantSlugValue, WebE2EFixture.OperatorEmail, WebE2EFixture.OperatorPassword);
        await page.WaitForURLAsync(_fixture.WebBaseUrl + "/");

        await page.GetByText(WebE2EFixture.OperatorFullName).First.WaitForAsync(new LocatorWaitForOptions { Timeout = 15000 });
        var condominiumsNavItem = page.GetByRole(AriaRole.Link, new() { Name = "Condomínios" });
        (await condominiumsNavItem.CountAsync()).Should().Be(0, "OPERATOR is never granted PROPERTIES:MANAGE, so the nav item must not render at all");

        await page.GotoAsync(_fixture.WebBaseUrl + "/condominiums");

        await page.WaitForURLAsync(_fixture.WebBaseUrl + "/forbidden");
        page.Url.Should().Be(_fixture.WebBaseUrl + "/forbidden", "the route guard must deny direct navigation by real permission, not just hide the nav link");
    }

    [Fact]
    public async Task An_authenticated_user_without_PROPERTIES_MANAGE_cannot_view_or_access_Imoveis()
    {
        var page = await NewPageAsync();
        await page.GotoAsync(_fixture.WebBaseUrl + "/login");
        await LoginAsync(page, WebE2EFixture.TenantSlugValue, WebE2EFixture.OperatorEmail, WebE2EFixture.OperatorPassword);
        await page.WaitForURLAsync(_fixture.WebBaseUrl + "/");

        await page.GetByText(WebE2EFixture.OperatorFullName).First.WaitForAsync(new LocatorWaitForOptions { Timeout = 15000 });
        var propertiesNavItem = page.GetByRole(AriaRole.Link, new() { Name = "Imóveis" });
        (await propertiesNavItem.CountAsync()).Should().Be(0, "OPERATOR is never granted PROPERTIES:MANAGE, so the nav item must not render at all");

        await page.GotoAsync(_fixture.WebBaseUrl + "/properties");

        await page.WaitForURLAsync(_fixture.WebBaseUrl + "/forbidden");
        page.Url.Should().Be(_fixture.WebBaseUrl + "/forbidden", "the route guard must deny direct navigation by real permission, not just hide the nav link");
    }

    [Fact]
    public async Task An_ADMIN_user_sees_both_Condominios_and_Imoveis_nav_items_and_can_reach_both_routes()
    {
        var page = await NewPageAsync();
        await page.GotoAsync(_fixture.WebBaseUrl + "/login");
        await LoginAsync(page, WebE2EFixture.TenantSlugValue, WebE2EFixture.AdminEmail, WebE2EFixture.AdminPassword);
        await page.WaitForURLAsync(_fixture.WebBaseUrl + "/");

        await page.GetByRole(AriaRole.Link, new() { Name = "Condomínios" }).ClickAsync();
        await page.WaitForURLAsync(_fixture.WebBaseUrl + "/condominiums");

        await page.GetByRole(AriaRole.Link, new() { Name = "Imóveis" }).ClickAsync();
        await page.WaitForURLAsync(_fixture.WebBaseUrl + "/properties");
    }
}
