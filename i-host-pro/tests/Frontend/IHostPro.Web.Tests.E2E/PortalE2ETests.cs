using FluentAssertions;
using Microsoft.Playwright;

namespace IHostPro.Web.Tests.E2E;

/// <summary>
/// Real-browser coverage for Fase 6 Incremento 2A (Portal da Faxineira) —
/// the dedicated, mobile-first self-service shell for the HOUSEKEEPER role.
/// Drives the real, unmodified <c>IHostPro.Web</c> against the real,
/// unmodified <c>IHostPro.Api</c> — see <see cref="WebE2EFixture"/>. Every
/// test creates its own throwaway property, housekeeper user and cleanings
/// directly through the real API — using the ADMIN's own real bearer token,
/// captured off a real network request — then drives only the HOUSEKEEPER's
/// own UI actions under test through the real Angular app, authenticated as
/// that housekeeper through the real login form with a known synthetic
/// password (never a hand-typed production-like secret). Mirrors
/// <see cref="HousekeepingE2ETests"/>'s data-seeding pattern exactly.
/// </summary>
[Collection(WebE2EFixtureCollection.Name)]
public sealed class PortalE2ETests
{
    private const string HousekeeperPassword = "Correct-Horse-Battery-Staple-99!";

    private readonly WebE2EFixture _fixture;

    public PortalE2ETests(WebE2EFixture fixture) => _fixture = fixture;

    private async Task<IPage> NewPageAsync()
    {
        var context = await _fixture.Browser.NewContextAsync();
        return await context.NewPageAsync();
    }

    /// <summary>Logs in as ADMIN and returns the page (left on the admin Home) plus the real bearer token — for API-level test-data setup only, never for driving the Portal UI.</summary>
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

    /// <summary>Creates a throwaway, ACTIVE property directly through the real API and waits until Housekeeping's own local projection knows about it. Returns its id.</summary>
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

        await WaitUntilKnownToHousekeepingAsync(page, bearerToken, propertyId);

        return propertyId;
    }

    /// <summary>Same asynchronous-projection wait as <see cref="HousekeepingE2ETests.WaitUntilKnownToHousekeepingAsync"/> — see that method's doc comment for why a bounded poll (never a fixed sleep) is required here.</summary>
    private async Task WaitUntilKnownToHousekeepingAsync(IPage page, string bearerToken, string propertyId)
    {
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
                var body = await probeResponse.JsonAsync();
                var probeCleaningId = body!.Value.GetProperty("id").GetString()!;
                var cancelResponse = await page.Context.APIRequest.PostAsync(
                    _fixture.ApiBaseUrl + $"/api/v1/cleanings/{probeCleaningId}/cancel",
                    new APIRequestContextOptions { Headers = new Dictionary<string, string> { ["Authorization"] = bearerToken } });
                cancelResponse.Ok.Should().BeTrue($"cleanup of the probe cleaning must succeed (status {cancelResponse.Status})");
                return;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(300));
        }

        throw new TimeoutException($"Property {propertyId} was never projected into Housekeeping's own local projection within 20s.");
    }

    /// <summary>Creates a throwaway HOUSEKEEPER user directly through the real API, with a known synthetic password — never a hand-typed production-like secret. Returns the created user's id.</summary>
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

    /// <summary>Creates a throwaway cleaning and assigns it to the given housekeeper, both directly through the real API. Returns the cleaning's id.</summary>
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

    /// <summary>Logs in as the given housekeeper by first navigating to the guarded /my-cleanings route while unauthenticated (so authGuard redirects to /login?redirectTo=%2Fmy-cleanings), then submitting the real login form — mirrors how a real housekeeper would land on the Portal from a bookmarked/shared link.</summary>
    private async Task<IPage> LoginAsHousekeeperOnPortalAsync(string email)
    {
        var page = await NewPageAsync();
        await page.GotoAsync(_fixture.WebBaseUrl + "/my-cleanings");
        await page.WaitForURLAsync(url => url.Contains("/login"));
        await page.GetByLabel("Empresa").FillAsync(WebE2EFixture.TenantSlugValue);
        await page.GetByLabel("E-mail").FillAsync(email);
        await page.GetByLabel("Senha").FillAsync(HousekeeperPassword);
        await page.GetByRole(AriaRole.Button, new() { Name = "Entrar" }).ClickAsync();
        await page.WaitForURLAsync(_fixture.WebBaseUrl + "/my-cleanings");
        return page;
    }

    [Fact]
    public async Task Housekeeper_completes_the_full_self_service_lifecycle_with_occurrence_and_checklist()
    {
        var (adminPage, token) = await LoginAsAdminAsync();
        var propertyId = await CreateActivePropertyViaApiAsync(adminPage, token, "E2E-PORTAL-LIFECYCLE-1", "E2E Portal Lifecycle Property");
        var housekeeperEmail = $"e2e-housekeeper-portal-{Guid.NewGuid():N}@e2e-playwright.test";
        var housekeeperUserId = await CreateHousekeeperUserViaApiAsync(adminPage, token, "E2E Portal Housekeeper", housekeeperEmail);
        var cleaningId = await CreateAssignedCleaningViaApiAsync(adminPage, token, propertyId, housekeeperUserId);

        var page = await LoginAsHousekeeperOnPortalAsync(housekeeperEmail);

        // Minhas Faxinas — the assigned cleaning renders, the admin area is never linked from here.
        var card = page.Locator("mat-card", new PageLocatorOptions { HasText = propertyId });
        await card.GetByText("Designada").WaitForAsync();

        // Detail — status-gated actions for Assigned.
        await card.ClickAsync();
        await page.WaitForURLAsync(_fixture.WebBaseUrl + $"/my-cleanings/{cleaningId}");
        var summary = page.Locator(".my-cleaning-detail__summary");
        await summary.GetByText("Designada").WaitForAsync();

        // InTransit
        await page.GetByRole(AriaRole.Button, new() { Name = "Estou a caminho" }).ClickAsync();
        await page.GetByText("Marcado como a caminho.").WaitForAsync();
        await summary.GetByText("A caminho").WaitForAsync();

        // Start
        await page.GetByRole(AriaRole.Button, new() { Name = "Iniciar", Exact = true }).ClickAsync();
        await page.GetByText("Faxina iniciada.").WaitForAsync();
        await summary.GetByText("Em andamento").WaitForAsync();

        // Register an occurrence while Started (non-terminal).
        await page.GetByRole(AriaRole.Button, new() { Name = "Registrar ocorrência" }).ClickAsync();
        await page.GetByLabel("Tipo").ClickAsync();
        await page.GetByRole(AriaRole.Option, new() { Name = "Dano" }).ClickAsync();
        await page.GetByLabel("Descrição (opcional)").FillAsync("Vaso quebrado no banheiro.");
        await page.GetByRole(AriaRole.Button, new() { Name = "Registrar", Exact = true }).ClickAsync();
        await page.GetByText("Ocorrência registrada.").WaitForAsync();
        await page.GetByText("Dano").WaitForAsync();

        // Toggle a checklist item.
        await page.GetByRole(AriaRole.Checkbox, new() { Name = "Fogão" }).CheckAsync();
        await page.GetByRole(AriaRole.Checkbox, new() { Name = "Fogão" }).IsCheckedAsync().ContinueWith(t => t.Result.Should().BeTrue());

        // StartInspection
        await page.GetByRole(AriaRole.Button, new() { Name = "Iniciar inspeção" }).ClickAsync();
        await page.GetByText("Inspeção iniciada.").WaitForAsync();
        await summary.GetByText("Em inspeção").WaitForAsync();

        // Complete — terminal, no further lifecycle actions offered.
        await page.GetByRole(AriaRole.Button, new() { Name = "Concluir" }).ClickAsync();
        await page.GetByText("Faxina concluída.").WaitForAsync();
        await summary.GetByText("Concluída").WaitForAsync();
        (await page.Locator(".my-cleaning-detail__actions button").CountAsync())
            .Should().Be(0, "a Completed cleaning is terminal — no lifecycle action button is offered");

        // Bottom navigation: back to the list without a full page reload.
        await page.GetByRole(AriaRole.Link, new() { Name = "Minhas Faxinas" }).ClickAsync();
        await page.WaitForURLAsync(_fixture.WebBaseUrl + "/my-cleanings");
        await page.Locator("mat-card", new PageLocatorOptions { HasText = propertyId }).GetByText("Concluída").WaitForAsync();

        // Logout.
        await page.GetByRole(AriaRole.Button, new() { Name = "Sair" }).ClickAsync();
        await page.WaitForURLAsync(_fixture.WebBaseUrl + "/login");
    }

    [Fact]
    public async Task Housekeeper_reports_a_delay_and_requests_materials_and_help()
    {
        var (adminPage, token) = await LoginAsAdminAsync();
        var propertyId = await CreateActivePropertyViaApiAsync(adminPage, token, "E2E-PORTAL-BRANCHES-1", "E2E Portal Branches Property");
        var housekeeperEmail = $"e2e-housekeeper-portal-{Guid.NewGuid():N}@e2e-playwright.test";
        var housekeeperUserId = await CreateHousekeeperUserViaApiAsync(adminPage, token, "E2E Portal Branches Housekeeper", housekeeperEmail);

        var delayCleaningId = await CreateAssignedCleaningViaApiAsync(adminPage, token, propertyId, housekeeperUserId);
        var materialsCleaningId = await CreateAssignedCleaningViaApiAsync(adminPage, token, propertyId, housekeeperUserId);
        var helpCleaningId = await CreateAssignedCleaningViaApiAsync(adminPage, token, propertyId, housekeeperUserId);

        var page = await LoginAsHousekeeperOnPortalAsync(housekeeperEmail);

        // Delay — reportable while Assigned (non-terminal), does not change status.
        await page.GotoAsync(_fixture.WebBaseUrl + $"/my-cleanings/{delayCleaningId}");
        var delaySummary = page.Locator(".my-cleaning-detail__summary");
        await delaySummary.GetByText("Designada").WaitForAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Informar atraso" }).ClickAsync();
        await page.GetByText("Atraso informado.").WaitForAsync();
        // Delay does not change the cleaning's status — it must still read "Designada".
        await delaySummary.GetByText("Designada").WaitForAsync();

        // Materials — only offered once Started.
        await page.GotoAsync(_fixture.WebBaseUrl + $"/my-cleanings/{materialsCleaningId}");
        var materialsSummary = page.Locator(".my-cleaning-detail__summary");
        await page.GetByRole(AriaRole.Button, new() { Name = "Iniciar", Exact = true }).ClickAsync();
        await materialsSummary.GetByText("Em andamento").WaitForAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Preciso de materiais" }).ClickAsync();
        await page.GetByText("Marcada como aguardando materiais.").WaitForAsync();
        await materialsSummary.GetByText("Aguardando materiais").WaitForAsync();

        // Help — only offered once Started.
        await page.GotoAsync(_fixture.WebBaseUrl + $"/my-cleanings/{helpCleaningId}");
        var helpSummary = page.Locator(".my-cleaning-detail__summary");
        await page.GetByRole(AriaRole.Button, new() { Name = "Iniciar", Exact = true }).ClickAsync();
        await helpSummary.GetByText("Em andamento").WaitForAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Preciso de ajuda" }).ClickAsync();
        await page.GetByText("Marcada como aguardando ajuda.").WaitForAsync();
        await helpSummary.GetByText("Aguardando ajuda").WaitForAsync();
    }

    [Fact]
    public async Task Portal_renders_full_width_at_a_mobile_viewport_with_no_horizontal_overflow()
    {
        var (adminPage, token) = await LoginAsAdminAsync();
        var propertyId = await CreateActivePropertyViaApiAsync(adminPage, token, "E2E-PORTAL-MOBILE-1", "E2E Portal Mobile Property");
        var housekeeperEmail = $"e2e-housekeeper-portal-{Guid.NewGuid():N}@e2e-playwright.test";
        var housekeeperUserId = await CreateHousekeeperUserViaApiAsync(adminPage, token, "E2E Portal Mobile Housekeeper", housekeeperEmail);
        await CreateAssignedCleaningViaApiAsync(adminPage, token, propertyId, housekeeperUserId);

        var page = await LoginAsHousekeeperOnPortalAsync(housekeeperEmail);
        await page.SetViewportSizeAsync(375, 812);
        await page.Locator("mat-card", new PageLocatorOptions { HasText = propertyId }).WaitForAsync();

        var overflow = await page.EvaluateAsync<bool>(
            "() => document.documentElement.scrollWidth > document.documentElement.clientWidth");
        overflow.Should().BeFalse("the mobile-first Portal must never require horizontal scrolling at a 375px viewport");

        var bottomNavWidth = await page.EvaluateAsync<double>(
            "() => document.querySelector('.portal-shell__bottom-nav').getBoundingClientRect().width");
        bottomNavWidth.Should().BeApproximately(375, 1, "the bottom navigation must span the full mobile viewport width");
    }
}
