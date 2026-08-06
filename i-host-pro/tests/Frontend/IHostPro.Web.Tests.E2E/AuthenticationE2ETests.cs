using FluentAssertions;
using Microsoft.Playwright;

namespace IHostPro.Web.Tests.E2E;

/// <summary>
/// Real-browser E2E coverage for Fase 4 Checkpoint 4's authentication flows
/// (Plano Executivo, Incremento 1) — login válido/inválido, rota protegida,
/// logout, restauração de sessão via refresh token após reload, e refresh
/// inválido redirecionando para login. Drives the real, unmodified
/// <c>IHostPro.Web</c> (served by its own <c>ng serve</c>) against the real,
/// unmodified <c>IHostPro.Api</c> — see <see cref="WebE2EFixture"/> for the
/// full composition. Every test gets its own isolated <see cref="IBrowserContext"/>
/// (fresh cookies/sessionStorage), so tests never leak session state into
/// each other regardless of execution order.
///
/// <see cref="WebE2EFixtureCollection"/>: shares one <see cref="WebE2EFixture"/>
/// instance with <see cref="UsersAuthorizationE2ETests"/> so xUnit never boots
/// two fixtures in parallel — both bind the same fixed RabbitMQ host port
/// (5672), so a second concurrent instance would fail to start.
/// </summary>
[Collection(WebE2EFixtureCollection.Name)]
public sealed class AuthenticationE2ETests
{
    private readonly WebE2EFixture _fixture;

    public AuthenticationE2ETests(WebE2EFixture fixture) => _fixture = fixture;

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
    public async Task Valid_login_reaches_the_authenticated_home_and_shows_the_logged_in_user()
    {
        var page = await NewPageAsync();
        await page.GotoAsync(_fixture.WebBaseUrl + "/login");

        await LoginAsync(page, WebE2EFixture.TenantSlugValue, WebE2EFixture.AdminEmail, WebE2EFixture.AdminPassword);

        await page.WaitForURLAsync(_fixture.WebBaseUrl + "/");
        var mainText = await page.Locator("main").InnerTextAsync();
        mainText.Should().Contain(WebE2EFixture.AdminFullName, "the home page must identify the authenticated user by name");
    }

    [Fact]
    public async Task Invalid_login_shows_an_error_and_never_leaves_the_login_page()
    {
        var page = await NewPageAsync();
        await page.GotoAsync(_fixture.WebBaseUrl + "/login");

        await page.GetByLabel("Empresa").FillAsync(WebE2EFixture.TenantSlugValue);
        await page.GetByLabel("E-mail").FillAsync(WebE2EFixture.AdminEmail);
        await page.GetByLabel("Senha").FillAsync("definitely-the-wrong-password");

        // Asserts on the real network response too, not just the rendered
        // text — this is what originally caught the NSwag-generated client
        // never surfacing HttpErrorResponse to the component (Fase 4
        // Checkpoint 4 homologação): the backend genuinely returned 401 while
        // the UI still showed the generic error message, because the
        // component's error classification checked the wrong type.
        var response = await page.RunAndWaitForResponseAsync(
            async () => await page.GetByRole(AriaRole.Button, new() { Name = "Entrar" }).ClickAsync(),
            resp => resp.Url.Contains("/api/v1/auth/login") && resp.Request.Method == "POST");
        response.Status.Should().Be(401, "a wrong password must be rejected as unauthorized, not any other status");

        var errorText = await page.Locator(".login-error").InnerTextAsync();
        errorText.Should().Be("E-mail, senha ou empresa inválidos.", "a 401 must render the invalid-credentials message, not the generic one");
        page.Url.Should().StartWith(_fixture.WebBaseUrl + "/login", "an invalid login must never navigate away from the login page");
    }

    [Fact]
    public async Task Navigating_to_a_protected_route_while_unauthenticated_redirects_to_login_with_the_original_path_preserved()
    {
        var page = await NewPageAsync();

        await page.GotoAsync(_fixture.WebBaseUrl + "/users");

        await page.WaitForURLAsync(url => url.Contains("/login"));
        page.Url.Should().Contain("redirectTo=%2Fusers", "the guard must preserve the originally requested path for post-login redirect");
    }

    [Fact]
    public async Task Logout_clears_the_session_and_a_subsequent_protected_navigation_redirects_to_login_again()
    {
        var page = await NewPageAsync();
        await page.GotoAsync(_fixture.WebBaseUrl + "/login");
        await LoginAsync(page, WebE2EFixture.TenantSlugValue, WebE2EFixture.AdminEmail, WebE2EFixture.AdminPassword);
        await page.WaitForURLAsync(_fixture.WebBaseUrl + "/");

        await page.GetByRole(AriaRole.Button, new() { Name = "Sair" }).ClickAsync();
        await page.WaitForURLAsync(url => url.Contains("/login"));

        await page.GotoAsync(_fixture.WebBaseUrl + "/users");
        await page.WaitForURLAsync(url => url.Contains("/login"));
        page.Url.Should().Contain("/login", "after logout, the local session must be cleared so protected routes redirect to login again");
    }

    [Fact]
    public async Task Reloading_the_page_after_login_restores_the_session_from_the_refresh_token_without_re_entering_credentials()
    {
        var page = await NewPageAsync();
        await page.GotoAsync(_fixture.WebBaseUrl + "/login");
        await LoginAsync(page, WebE2EFixture.TenantSlugValue, WebE2EFixture.AdminEmail, WebE2EFixture.AdminPassword);
        await page.WaitForURLAsync(_fixture.WebBaseUrl + "/");

        // A full reload discards the in-memory access token by design
        // (Fase 4 approved decision: access token in memory only) — only the
        // sessionStorage-persisted refresh token can restore the session.
        // The URL itself never changes across a reload of "/", so waiting on
        // it (unlike the other redirect-based tests) would resolve
        // immediately, before the async restoreSession()/GET-users-me
        // round-trip finishes — wait on the actual restored content instead.
        await page.ReloadAsync();

        await page.GetByText(WebE2EFixture.AdminFullName).First.WaitForAsync(new LocatorWaitForOptions { Timeout = 15000 });
        page.Url.Should().Be(_fixture.WebBaseUrl + "/", "a reload must restore the session via the refresh token, not bounce to login");
    }

    [Fact]
    public async Task Reloading_with_an_invalid_refresh_token_redirects_to_login()
    {
        var page = await NewPageAsync();
        await page.GotoAsync(_fixture.WebBaseUrl + "/login");
        await LoginAsync(page, WebE2EFixture.TenantSlugValue, WebE2EFixture.AdminEmail, WebE2EFixture.AdminPassword);
        await page.WaitForURLAsync(_fixture.WebBaseUrl + "/");

        await page.EvaluateAsync("sessionStorage.setItem('ihostpro.refreshToken', 'this-is-not-a-valid-refresh-token')");
        await page.ReloadAsync();

        await page.WaitForURLAsync(url => url.Contains("/login"));
        page.Url.Should().Contain("/login", "an invalid/rejected refresh token must fall back to login, never leave a broken authenticated shell up");
    }
}
