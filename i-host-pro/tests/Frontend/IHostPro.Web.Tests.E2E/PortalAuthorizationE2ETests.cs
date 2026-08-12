using FluentAssertions;
using Microsoft.Playwright;

namespace IHostPro.Web.Tests.E2E;

/// <summary>
/// Real-browser coverage for the Portal da Faxineira's fail-closed
/// authorization boundary (Fase 6 Incremento 2A, approval §5-6/§21.3): a
/// HOUSEKEEPER must never reach the administrative area (gated on
/// <c>CLEANINGS:MANAGE</c>, never granted to this role), and must never see
/// another housekeeper's own Cleaning even by direct URL — the own-cleaning
/// ABAC guarantee already covered at the API level by
/// <c>OwnCleaningLoader</c>/<c>GetByIdForHousekeeperAsync</c> (uniform 404,
/// never a distinguishing 403) is re-verified here end-to-end through the
/// real UI. Drives the real, unmodified <c>IHostPro.Web</c> against the
/// real, unmodified <c>IHostPro.Api</c> — see <see cref="WebE2EFixture"/>.
/// Mirrors <see cref="UsersAuthorizationE2ETests"/>'s structure exactly.
/// </summary>
[Collection(WebE2EFixtureCollection.Name)]
public sealed class PortalAuthorizationE2ETests
{
    private const string HousekeeperPassword = "Correct-Horse-Battery-Staple-99!";

    private readonly WebE2EFixture _fixture;

    public PortalAuthorizationE2ETests(WebE2EFixture fixture) => _fixture = fixture;

    private async Task<IPage> NewPageAsync()
    {
        var context = await _fixture.Browser.NewContextAsync();
        return await context.NewPageAsync();
    }

    private async Task<(IPage Page, string BearerToken)> LoginAsAdminAsync()
    {
        var page = await NewPageAsync();
        await page.GotoAsync(_fixture.WebBaseUrl + "/login");
        await page.GetByLabel("Empresa").FillAsync(WebE2EFixture.TenantSlugValue);
        await page.GetByLabel("E-mail").FillAsync(WebE2EFixture.AdminEmail);
        await page.GetByLabel("Senha").FillAsync(WebE2EFixture.AdminPassword);

        var profileRequest = await page.RunAndWaitForRequestAsync(
            async () => await page.GetByRole(AriaRole.Button, new() { Name = "Entrar" }).ClickAsync(),
            req => req.Url.Contains("/api/v1/users/me") && req.Method == "GET");
        var bearerToken = await profileRequest.HeaderValueAsync("Authorization") ?? throw new InvalidOperationException("No Authorization header captured.");

        await page.WaitForURLAsync(_fixture.WebBaseUrl + "/");
        return (page, bearerToken);
    }

    private async Task<string> CreateHousekeeperUserViaApiAsync(IPage page, string bearerToken, string fullName, string email)
    {
        var response = await page.Context.APIRequest.PostAsync(
            _fixture.ApiBaseUrl + "/api/v1/users",
            new APIRequestContextOptions
            {
                Headers = new Dictionary<string, string> { ["Authorization"] = bearerToken },
                DataObject = new { fullName, email, initialPassword = HousekeeperPassword, roleCode = "HOUSEKEEPER" },
            });
        response.Ok.Should().BeTrue($"test-data setup via the real API must succeed (status {response.Status})");
        var body = await response.JsonAsync();
        return body!.Value.GetProperty("id").GetString()!;
    }

    private static readonly object SampleAddress = new
    {
        zipCode = "01000-000",
        street = "Rua das Flores",
        number = "100",
        neighborhood = "Centro",
        city = "Sao Paulo",
        state = "SP",
        country = "BR",
    };

    private async Task<string> CreateActivePropertyViaApiAsync(IPage page, string bearerToken, string code, string name)
    {
        var createResponse = await page.Context.APIRequest.PostAsync(
            _fixture.ApiBaseUrl + "/api/v1/properties",
            new APIRequestContextOptions
            {
                Headers = new Dictionary<string, string> { ["Authorization"] = bearerToken },
                DataObject = new { code, name, capacity = 4, condominiumId = (string?)null, address = SampleAddress },
            });
        createResponse.Ok.Should().BeTrue($"test-data setup via the real API must succeed (status {createResponse.Status})");
        var body = await createResponse.JsonAsync();
        var propertyId = body!.Value.GetProperty("id").GetString()!;

        var activateResponse = await page.Context.APIRequest.PostAsync(
            _fixture.ApiBaseUrl + $"/api/v1/properties/{propertyId}/activate",
            new APIRequestContextOptions { Headers = new Dictionary<string, string> { ["Authorization"] = bearerToken } });
        activateResponse.Ok.Should().BeTrue($"test-data setup via the real API must succeed (status {activateResponse.Status})");

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            var probeResponse = await page.Context.APIRequest.PostAsync(
                _fixture.ApiBaseUrl + "/api/v1/cleanings",
                new APIRequestContextOptions
                {
                    Headers = new Dictionary<string, string> { ["Authorization"] = bearerToken },
                    DataObject = new { propertyId, reservationId = (string?)null },
                });
            if (probeResponse.Ok)
            {
                var body2 = await probeResponse.JsonAsync();
                var probeCleaningId = body2!.Value.GetProperty("id").GetString()!;
                var cancelResponse = await page.Context.APIRequest.PostAsync(
                    _fixture.ApiBaseUrl + $"/api/v1/cleanings/{probeCleaningId}/cancel",
                    new APIRequestContextOptions { Headers = new Dictionary<string, string> { ["Authorization"] = bearerToken } });
                cancelResponse.Ok.Should().BeTrue($"cleanup of the probe cleaning must succeed (status {cancelResponse.Status})");
                return propertyId;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(300));
        }

        throw new TimeoutException($"Property {propertyId} was never projected into Housekeeping's own local projection within 20s.");
    }

    private async Task<string> CreateAssignedCleaningViaApiAsync(IPage page, string bearerToken, string propertyId, string housekeeperUserId)
    {
        var createResponse = await page.Context.APIRequest.PostAsync(
            _fixture.ApiBaseUrl + "/api/v1/cleanings",
            new APIRequestContextOptions
            {
                Headers = new Dictionary<string, string> { ["Authorization"] = bearerToken },
                DataObject = new { propertyId, reservationId = (string?)null },
            });
        createResponse.Ok.Should().BeTrue($"test-data setup via the real API must succeed (status {createResponse.Status})");
        var body = await createResponse.JsonAsync();
        var cleaningId = body!.Value.GetProperty("id").GetString()!;

        var assignResponse = await page.Context.APIRequest.PostAsync(
            _fixture.ApiBaseUrl + $"/api/v1/cleanings/{cleaningId}/assign",
            new APIRequestContextOptions
            {
                Headers = new Dictionary<string, string> { ["Authorization"] = bearerToken },
                DataObject = new { housekeeperUserId },
            });
        assignResponse.Ok.Should().BeTrue($"test-data setup via the real API must succeed (status {assignResponse.Status})");

        return cleaningId;
    }

    private static async Task LoginAsync(IPage page, string tenantSlug, string email, string password)
    {
        await page.GetByLabel("Empresa").FillAsync(tenantSlug);
        await page.GetByLabel("E-mail").FillAsync(email);
        await page.GetByLabel("Senha").FillAsync(password);
        await page.GetByRole(AriaRole.Button, new() { Name = "Entrar" }).ClickAsync();
    }

    [Fact]
    public async Task An_unauthenticated_visitor_to_my_cleanings_is_redirected_to_login_with_the_path_preserved()
    {
        var page = await NewPageAsync();

        await page.GotoAsync(_fixture.WebBaseUrl + "/my-cleanings");

        await page.WaitForURLAsync(url => url.Contains("/login"));
        page.Url.Should().Contain("redirectTo=%2Fmy-cleanings", "the guard must preserve the originally requested Portal path for post-login redirect");
    }

    [Fact]
    public async Task A_HOUSEKEEPER_who_navigates_directly_to_the_administrative_housekeeping_area_is_denied_access()
    {
        var (adminPage, token) = await LoginAsAdminAsync();
        var housekeeperEmail = $"e2e-housekeeper-authz-{Guid.NewGuid():N}@e2e-playwright.test";
        await CreateHousekeeperUserViaApiAsync(adminPage, token, "E2E Portal Authz Housekeeper", housekeeperEmail);

        var page = await NewPageAsync();
        await page.GotoAsync(_fixture.WebBaseUrl + "/login");
        await LoginAsync(page, WebE2EFixture.TenantSlugValue, housekeeperEmail, HousekeeperPassword);
        // Login with no redirectTo lands on the admin Home ('/') regardless of role — see Login.submit().
        await page.WaitForURLAsync(_fixture.WebBaseUrl + "/");

        await page.GotoAsync(_fixture.WebBaseUrl + "/housekeeping");

        await page.WaitForURLAsync(_fixture.WebBaseUrl + "/forbidden");
        page.Url.Should().Be(_fixture.WebBaseUrl + "/forbidden", "HOUSEKEEPER holds only CLEANINGS:MANAGE:OWN_CLEANING — the administrative route requires the distinct CLEANINGS:MANAGE permission and must never be reachable by prefix-matching one against the other");
    }

    [Fact]
    public async Task A_HOUSEKEEPER_cannot_load_a_cleaning_assigned_to_a_different_housekeeper()
    {
        var (adminPage, token) = await LoginAsAdminAsync();
        var propertyId = await CreateActivePropertyViaApiAsync(adminPage, token, "E2E-PORTAL-ISOLATION-1", "E2E Portal Isolation Property");

        var ownerEmail = $"e2e-housekeeper-owner-{Guid.NewGuid():N}@e2e-playwright.test";
        var ownerUserId = await CreateHousekeeperUserViaApiAsync(adminPage, token, "E2E Portal Owner Housekeeper", ownerEmail);
        var otherCleaningId = await CreateAssignedCleaningViaApiAsync(adminPage, token, propertyId, ownerUserId);

        var outsiderEmail = $"e2e-housekeeper-outsider-{Guid.NewGuid():N}@e2e-playwright.test";
        await CreateHousekeeperUserViaApiAsync(adminPage, token, "E2E Portal Outsider Housekeeper", outsiderEmail);

        var outsiderPage = await NewPageAsync();
        await outsiderPage.GotoAsync(_fixture.WebBaseUrl + "/login");
        await outsiderPage.GetByLabel("Empresa").FillAsync(WebE2EFixture.TenantSlugValue);
        await outsiderPage.GetByLabel("E-mail").FillAsync(outsiderEmail);
        await outsiderPage.GetByLabel("Senha").FillAsync(HousekeeperPassword);
        var profileRequest = await outsiderPage.RunAndWaitForRequestAsync(
            async () => await outsiderPage.GetByRole(AriaRole.Button, new() { Name = "Entrar" }).ClickAsync(),
            req => req.Url.Contains("/api/v1/users/me") && req.Method == "GET");
        var outsiderBearerToken = await profileRequest.HeaderValueAsync("Authorization") ?? throw new InvalidOperationException("No Authorization header captured.");
        // Login with no redirectTo lands on the admin Home ('/') regardless of role — see Login.submit().
        await outsiderPage.WaitForURLAsync(_fixture.WebBaseUrl + "/");

        var listResponse = await outsiderPage.RunAndWaitForResponseAsync(
            async () => await outsiderPage.GotoAsync(_fixture.WebBaseUrl + "/my-cleanings"),
            r => r.Url.Contains("/api/v1/my-cleanings") && r.Request.Method == "GET");
        listResponse.Ok.Should().BeTrue($"the real list call must succeed (status {listResponse.Status})");

        // The other housekeeper's Cleaning must never appear in this housekeeper's own list.
        (await outsiderPage.Locator("mat-card", new PageLocatorOptions { HasText = propertyId }).CountAsync())
            .Should().Be(0, "Minhas Faxinas must only ever list the caller's own Cleanings");

        // A direct API call for the other housekeeper's cleaning must fail closed — real HTTP call, uniform not-found, never the real data or a distinguishing 403.
        var detailResponse = await outsiderPage.Context.APIRequest.GetAsync(
            _fixture.ApiBaseUrl + $"/api/v1/my-cleanings/{otherCleaningId}",
            new APIRequestContextOptions { Headers = new Dictionary<string, string> { ["Authorization"] = outsiderBearerToken } });
        detailResponse.Status.Should().Be(404, "OwnCleaningLoader must return the same not-found result whether the cleaning doesn't exist or simply isn't the caller's own — never a distinguishing 403");

        // The real UI reaches the same conclusion when navigated to directly.
        await outsiderPage.GotoAsync(_fixture.WebBaseUrl + $"/my-cleanings/{otherCleaningId}");
        await outsiderPage.GetByText("Não foi possível carregar esta faxina.").WaitForAsync(new LocatorWaitForOptions { Timeout = 15000 });
    }
}
