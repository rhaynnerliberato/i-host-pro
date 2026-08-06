using FluentAssertions;
using Microsoft.Playwright;

namespace IHostPro.Web.Tests.E2E;

/// <summary>
/// Real-browser coverage for the authorization fix required before the rest
/// of Fase 4 Incremento 2 (Administração de Usuários) could proceed: the
/// "Usuários" nav item and the <c>/users</c> route must be gated on the
/// user's real effective permissions (<c>OwnProfileResponse.permissions</c>,
/// resolved server-side via <c>IPermissionReader</c> from
/// <c>GET /api/v1/users/me</c>) — never on a role name, never on merely being
/// authenticated. Drives the real, unmodified <c>IHostPro.Web</c> against the
/// real, unmodified <c>IHostPro.Api</c> — see <see cref="WebE2EFixture"/>.
/// These four tests are required to pass before any further Incremento 2
/// frontend flow (create/edit/block/role-management/reset-password) is built.
///
/// <see cref="WebE2EFixtureCollection"/>: shares one <see cref="WebE2EFixture"/>
/// instance with <see cref="AuthenticationE2ETests"/> so xUnit never boots two
/// fixtures in parallel — both bind the same fixed RabbitMQ host port (5672),
/// so a second concurrent instance would fail to start.
/// </summary>
[Collection(WebE2EFixtureCollection.Name)]
public sealed class UsersAuthorizationE2ETests
{
    private readonly WebE2EFixture _fixture;

    public UsersAuthorizationE2ETests(WebE2EFixture fixture) => _fixture = fixture;

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
    public async Task An_ADMIN_user_sees_the_Usuarios_nav_item_and_can_reach_the_users_route()
    {
        var page = await NewPageAsync();
        await page.GotoAsync(_fixture.WebBaseUrl + "/login");
        await LoginAsync(page, WebE2EFixture.TenantSlugValue, WebE2EFixture.AdminEmail, WebE2EFixture.AdminPassword);
        await page.WaitForURLAsync(_fixture.WebBaseUrl + "/");

        await page.GetByRole(AriaRole.Link, new() { Name = "Usuários" }).WaitForAsync();

        await page.GetByRole(AriaRole.Link, new() { Name = "Usuários" }).ClickAsync();

        await page.WaitForURLAsync(_fixture.WebBaseUrl + "/users");
        page.Url.Should().Be(_fixture.WebBaseUrl + "/users", "an ADMIN holds USERS:MANAGE, so the guard must let the navigation through");
    }

    [Fact]
    public async Task An_authenticated_user_without_USERS_MANAGE_does_not_see_the_Usuarios_nav_item()
    {
        var page = await NewPageAsync();
        await page.GotoAsync(_fixture.WebBaseUrl + "/login");
        await LoginAsync(page, WebE2EFixture.TenantSlugValue, WebE2EFixture.OperatorEmail, WebE2EFixture.OperatorPassword);
        await page.WaitForURLAsync(_fixture.WebBaseUrl + "/");

        // Wait for the profile-dependent part of the toolbar to render before asserting
        // absence, so this cannot pass merely because the nav list hasn't loaded yet.
        await page.GetByText(WebE2EFixture.OperatorFullName).First.WaitForAsync(new LocatorWaitForOptions { Timeout = 15000 });

        var usersNavItem = page.GetByRole(AriaRole.Link, new() { Name = "Usuários" });
        (await usersNavItem.CountAsync()).Should().Be(0, "OPERATOR is never granted USERS:MANAGE, so the nav item must not render at all");
    }

    [Fact]
    public async Task An_authenticated_user_without_USERS_MANAGE_who_navigates_directly_to_users_is_denied_access()
    {
        var page = await NewPageAsync();
        await page.GotoAsync(_fixture.WebBaseUrl + "/login");
        await LoginAsync(page, WebE2EFixture.TenantSlugValue, WebE2EFixture.OperatorEmail, WebE2EFixture.OperatorPassword);
        await page.WaitForURLAsync(_fixture.WebBaseUrl + "/");

        await page.GotoAsync(_fixture.WebBaseUrl + "/users");

        await page.WaitForURLAsync(_fixture.WebBaseUrl + "/forbidden");
        page.Url.Should().Be(_fixture.WebBaseUrl + "/forbidden", "the route guard must deny access by real permission, not just hide the nav link");
    }

    [Fact]
    public async Task Manually_typing_the_users_URL_never_bypasses_the_backend_even_if_the_frontend_guard_were_somehow_skipped()
    {
        var page = await NewPageAsync();
        await page.GotoAsync(_fixture.WebBaseUrl + "/login");
        await page.GetByLabel("Empresa").FillAsync(WebE2EFixture.TenantSlugValue);
        await page.GetByLabel("E-mail").FillAsync(WebE2EFixture.OperatorEmail);
        await page.GetByLabel("Senha").FillAsync(WebE2EFixture.OperatorPassword);

        // Captures the real Authorization header the app itself sends on the
        // GET /api/v1/users/me call that AuthService.login() issues right
        // after a successful login — never a decoded/reconstructed token.
        var profileRequest = await page.RunAndWaitForRequestAsync(
            async () => await page.GetByRole(AriaRole.Button, new() { Name = "Entrar" }).ClickAsync(),
            req => req.Url.Contains("/api/v1/users/me") && req.Method == "GET");
        var authorizationHeader = await profileRequest.HeaderValueAsync("Authorization");
        authorizationHeader.Should().NotBeNullOrEmpty("login must issue an authenticated GET /api/v1/users/me carrying the OPERATOR's real access token");

        await page.WaitForURLAsync(_fixture.WebBaseUrl + "/");

        // Calls the real API directly with that token, bypassing the Angular
        // router/guard entirely — proves the backend's own
        // [Authorize(Policy = USERS:MANAGE)] is the actual enforcement point,
        // not a UI-only convenience the router could be tricked into skipping.
        var response = await page.Context.APIRequest.GetAsync(
            _fixture.ApiBaseUrl + "/api/v1/users?page=1&pageSize=10",
            new APIRequestContextOptions { Headers = new Dictionary<string, string> { ["Authorization"] = authorizationHeader! } });

        response.Status.Should().Be(403, "the backend's own USERS:MANAGE policy must reject the OPERATOR regardless of what the frontend router does");
    }
}
