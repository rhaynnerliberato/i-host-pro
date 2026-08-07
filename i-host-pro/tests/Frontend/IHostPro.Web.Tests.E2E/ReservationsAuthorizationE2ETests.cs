using FluentAssertions;
using Microsoft.Playwright;

namespace IHostPro.Web.Tests.E2E;

/// <summary>
/// Real-browser coverage for the authorization gate required before the rest
/// of Fase 4 Incremento 4 (Gestão de Reservas) could proceed: the "Reservas"
/// nav item and the <c>/reservations</c> route must be gated on the user's
/// real effective permissions (<c>RESERVATIONS:MANAGE</c>, resolved
/// server-side via <c>IPermissionReader</c> from <c>GET /api/v1/users/me</c>)
/// — never on a role name, never on merely being authenticated.
///
/// Unlike <see cref="PropertyManagementAuthorizationE2ETests"/>, the seeded
/// OPERATOR cannot stand in for "a user without the permission" here:
/// <c>IdentityCatalogSeed</c> grants OPERATOR <c>RESERVATIONS:MANAGE</c>
/// directly (Documento 09 §6 — OPERATOR may "Cadastrar reservas manuais").
/// The real seeded role that never holds it is <c>PROPERTY_OWNER</c>
/// (Documento 09 §8 — only <c>RESERVATIONS:READ:OWN_OWNER</c>), so this suite
/// creates a throwaway PROPERTY_OWNER user through the real API — the exact
/// same pattern <c>PropertyManagementE2ETests.CreateEligibleOwnerUserViaApiAsync</c>
/// already established for test-data setup, never fabricated.
///
/// <see cref="WebE2EFixtureCollection"/>: shares one <see cref="WebE2EFixture"/>
/// instance with the rest of this suite so xUnit never boots two fixtures in
/// parallel — both bind the same fixed RabbitMQ host port (5672).
/// </summary>
[Collection(WebE2EFixtureCollection.Name)]
public sealed class ReservationsAuthorizationE2ETests
{
    private readonly WebE2EFixture _fixture;

    public ReservationsAuthorizationE2ETests(WebE2EFixture fixture) => _fixture = fixture;

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

    /// <summary>Logs in as ADMIN and returns the real bearer token the app itself used for GET /api/v1/users/me — for API-level test-data setup only, never for driving the UI.</summary>
    private async Task<string> LoginAsAdminAndCaptureTokenAsync(IPage page)
    {
        await page.GotoAsync(_fixture.WebBaseUrl + "/login");
        await page.GetByLabel("Empresa").FillAsync(WebE2EFixture.TenantSlugValue);
        await page.GetByLabel("E-mail").FillAsync(WebE2EFixture.AdminEmail);
        await page.GetByLabel("Senha").FillAsync(WebE2EFixture.AdminPassword);

        var profileRequest = await page.RunAndWaitForRequestAsync(
            async () => await page.GetByRole(AriaRole.Button, new() { Name = "Entrar" }).ClickAsync(),
            req => req.Url.Contains("/api/v1/users/me") && req.Method == "GET");
        var bearerToken = await profileRequest.HeaderValueAsync("Authorization") ?? throw new InvalidOperationException("No Authorization header captured.");

        await page.WaitForURLAsync(_fixture.WebBaseUrl + "/");
        return bearerToken;
    }

    /// <summary>Creates a throwaway user holding PROPERTY_OWNER directly through the real API — the only real seeded role that never holds RESERVATIONS:MANAGE. Returns its email so a second, independent session can log in as it.</summary>
    private async Task<string> CreatePropertyOwnerUserViaApiAsync(IPage page, string bearerToken)
    {
        var email = $"reservations-owner-{Guid.NewGuid():N}@e2e-playwright.test";
        var response = await page.Context.APIRequest.PostAsync(
            _fixture.ApiBaseUrl + "/api/v1/users",
            new APIRequestContextOptions
            {
                Headers = new Dictionary<string, string> { ["Authorization"] = bearerToken },
                DataObject = new
                {
                    fullName = "E2E Reservations Property Owner",
                    email,
                    initialPassword = PropertyOwnerPassword,
                    roleCode = "PROPERTY_OWNER",
                },
            });
        response.Ok.Should().BeTrue($"test-data setup via the real API must succeed (status {response.Status})");
        return email;
    }

    private const string PropertyOwnerPassword = "Correct-Horse-Battery-Staple-66!";

    [Fact]
    public async Task An_authenticated_user_without_RESERVATIONS_MANAGE_cannot_view_or_access_Reservas()
    {
        var adminPage = await NewPageAsync();
        var adminToken = await LoginAsAdminAndCaptureTokenAsync(adminPage);
        var ownerEmail = await CreatePropertyOwnerUserViaApiAsync(adminPage, adminToken);

        var page = await NewPageAsync();
        await page.GotoAsync(_fixture.WebBaseUrl + "/login");
        await LoginAsync(page, WebE2EFixture.TenantSlugValue, ownerEmail, PropertyOwnerPassword);
        await page.WaitForURLAsync(_fixture.WebBaseUrl + "/");

        var reservationsNavItem = page.GetByRole(AriaRole.Link, new() { Name = "Reservas" });
        (await reservationsNavItem.CountAsync()).Should().Be(0, "PROPERTY_OWNER is never granted RESERVATIONS:MANAGE, so the nav item must not render at all");

        await page.GotoAsync(_fixture.WebBaseUrl + "/reservations");

        await page.WaitForURLAsync(_fixture.WebBaseUrl + "/forbidden");
        page.Url.Should().Be(_fixture.WebBaseUrl + "/forbidden", "the route guard must deny direct navigation by real permission, not just hide the nav link");
    }

    [Fact]
    public async Task An_ADMIN_user_sees_the_Reservas_nav_item_and_can_reach_the_route()
    {
        var page = await NewPageAsync();
        await page.GotoAsync(_fixture.WebBaseUrl + "/login");
        await LoginAsync(page, WebE2EFixture.TenantSlugValue, WebE2EFixture.AdminEmail, WebE2EFixture.AdminPassword);
        await page.WaitForURLAsync(_fixture.WebBaseUrl + "/");

        await page.GetByRole(AriaRole.Link, new() { Name = "Reservas" }).ClickAsync();
        await page.WaitForURLAsync(_fixture.WebBaseUrl + "/reservations");
    }
}
