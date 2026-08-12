using FluentAssertions;
using Microsoft.Playwright;

namespace IHostPro.Web.Tests.E2E;

/// <summary>
/// Real-browser coverage for Fase 6 Incremento 1 (Housekeeping) — listagem
/// com filtros, criação manual, ciclo de vida administrativo completo
/// (Assign/Start/StartInspection/Complete), erros reais que o backend
/// distingue (imóvel desconhecido, camareira inelegível), e cancelamento com
/// confirmação. Drives the real, unmodified <c>IHostPro.Web</c> against the
/// real, unmodified <c>IHostPro.Api</c> — see <see cref="WebE2EFixture"/>.
/// Every test creates its own throwaway property (and, where needed, its own
/// housekeeper user) directly through the real API — using the ADMIN's own
/// real bearer token, captured off a real network request — then drives only
/// the one UI action under test through the real Angular app. Mirrors
/// <see cref="ReservationsE2ETests"/>'s pattern exactly.
/// </summary>
[Collection(WebE2EFixtureCollection.Name)]
public sealed class HousekeepingE2ETests
{
    private readonly WebE2EFixture _fixture;

    public HousekeepingE2ETests(WebE2EFixture fixture) => _fixture = fixture;

    private async Task<IPage> NewPageAsync()
    {
        var context = await _fixture.Browser.NewContextAsync();
        return await context.NewPageAsync();
    }

    /// <summary>Logs in as ADMIN and returns the page positioned on /housekeeping, plus the real bearer token the app itself used for GET /api/v1/users/me — for API-level test-data setup only, never for driving the UI.</summary>
    private async Task<(IPage Page, string BearerToken)> LoginAsAdminOnHousekeepingAsync()
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
        await page.GetByRole(AriaRole.Link, new() { Name = "Limpezas" }).ClickAsync();
        await page.WaitForURLAsync(_fixture.WebBaseUrl + "/housekeeping");

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

    /// <summary>Creates a throwaway, ACTIVE property directly through the real API — CreateCleaning rejects an unknown/inactive property with housekeeping.form.errors.propertyNotFound. Returns its id.</summary>
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

    /// <summary>
    /// PropertyManagement's own PropertyActivated event reaches Housekeeping's
    /// local property_projection asynchronously, over a real RabbitMQ round
    /// trip to the real Worker — CreateCleaning rejects a property that
    /// hasn't been projected yet with 404 property_not_found, even though the
    /// property was already successfully activated via the (synchronous) HTTP
    /// call above. There is no read endpoint exposing that projection's own
    /// state directly, so this polls the real behavior it actually gates on:
    /// a throwaway cleaning create-then-cancel, retried until it succeeds.
    /// Never a fixed sleep — bounded real polling against the actual
    /// asynchronous condition every other test in this file depends on.
    /// </summary>
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

    /// <summary>Creates a throwaway user with the HOUSEKEEPER role directly through the real API — mirrors UsersManagementE2ETests.CreateUserViaApiAsync exactly. Returns the created user's id.</summary>
    private async Task<string> CreateHousekeeperUserViaApiAsync(IPage page, string bearerToken, string fullName, string email)
    {
        var response = await page.Context.APIRequest.PostAsync(
            _fixture.ApiBaseUrl + "/api/v1/users",
            new APIRequestContextOptions
            {
                Headers = new Dictionary<string, string> { ["Authorization"] = bearerToken },
                DataObject = new { fullName, email, initialPassword = "Correct-Horse-Battery-Staple-99!", roleCode = "HOUSEKEEPER" },
            });
        response.Ok.Should().BeTrue($"test-data setup via the real API must succeed (status {response.Status})");
        var body = await response.JsonAsync();
        return body!.Value.GetProperty("id").GetString()!;
    }

    /// <summary>Creates a throwaway cleaning directly through the real API. Returns its id.</summary>
    private async Task<string> CreateCleaningViaApiAsync(IPage page, string bearerToken, string propertyId)
    {
        var response = await page.Context.APIRequest.PostAsync(
            _fixture.ApiBaseUrl + "/api/v1/cleanings",
            new APIRequestContextOptions
            {
                Headers = new Dictionary<string, string> { ["Authorization"] = bearerToken },
                DataObject = new { propertyId, reservationId = (string?)null },
            });
        response.Ok.Should().BeTrue($"test-data setup via the real API must succeed (status {response.Status})");
        var body = await response.JsonAsync();
        return body!.Value.GetProperty("id").GetString()!;
    }

    private static async Task<string> AlertTextAsync(ILocator dialog)
    {
        var alert = dialog.GetByRole(AriaRole.Alert);
        await alert.WaitForAsync();
        return (await alert.TextContentAsync())!;
    }

    /// <summary>
    /// Scopes the list to a single property via the real "ID do imóvel"
    /// filter field — never a raw unfiltered read of the default (oldest-
    /// first, page size 10) list. As this test class's own tests accumulate
    /// cleanings across a single sequential run (each property carries at
    /// least the WaitUntilKnownToHousekeepingAsync probe's own throwaway
    /// Cancelled cleaning, on top of whatever real cleanings each test
    /// creates), a later-running test's newly created row can fall past page
    /// 1 of the unfiltered list and never render in the DOM at all. Filtering
    /// by the exact propertyId sidesteps that regardless of run order or how
    /// many other properties/cleanings already exist.
    /// </summary>
    private static async Task FilterByPropertyIdAsync(IPage page, string propertyId)
    {
        await page.GetByLabel("ID do imóvel").FillAsync(propertyId);
        // Waits for the real GET /api/v1/cleanings response the "Filtrar"
        // click triggers, not just "the click completed" — the list state
        // machine goes through 'loading' (which removes the <table> from the
        // DOM entirely, per cleanings-list.html's @switch) before 'loaded'
        // re-renders it with the filtered rows. A caller that proceeds
        // immediately after the click can catch either the stale pre-filter
        // table or the brief loading gap, both racy; only the response
        // itself is a reliable signal that the filtered rows have landed.
        await page.RunAndWaitForResponseAsync(
            async () => await page.GetByRole(AriaRole.Button, new() { Name = "Filtrar" }).ClickAsync(),
            r => r.Url.Contains("/api/v1/cleanings") && r.Request.Method == "GET");
    }

    [Fact]
    public async Task Admin_views_the_cleanings_listing()
    {
        var (page, token) = await LoginAsAdminOnHousekeepingAsync();
        var propertyId = await CreateActivePropertyViaApiAsync(page, token, "E2E-HK-LIST-1", "E2E Housekeeping Listing Property");
        await CreateCleaningViaApiAsync(page, token, propertyId);

        await page.ReloadAsync();
        await FilterByPropertyIdAsync(page, propertyId);

        var row = page.Locator("tr", new PageLocatorOptions { HasText = propertyId }).Filter(new LocatorFilterOptions { HasText = "Pendente" });
        await row.WaitForAsync();
        (await row.GetByText("Pendente").CountAsync()).Should().BeGreaterThan(0, "a newly-created cleaning starts Pending");
    }

    [Fact]
    public async Task Admin_creates_a_cleaning_manually()
    {
        var (page, token) = await LoginAsAdminOnHousekeepingAsync();
        var propertyId = await CreateActivePropertyViaApiAsync(page, token, "E2E-HK-CREATE-1", "E2E Housekeeping Create Property");

        await page.GetByRole(AriaRole.Button, new() { Name = "Nova limpeza" }).ClickAsync();
        var dialog = page.GetByRole(AriaRole.Dialog);
        await dialog.GetByLabel("ID do imóvel").FillAsync(propertyId);
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Salvar" }).ClickAsync();

        await page.GetByText("Limpeza criada com sucesso.").WaitForAsync();
        await FilterByPropertyIdAsync(page, propertyId);
        // .First, not a bare WaitForAsync: this property also carries the
        // WaitUntilKnownToHousekeepingAsync readiness probe's own throwaway
        // Cancelled cleaning, so the propertyId text legitimately matches 2
        // rows here — this assertion only needs to prove the new one exists.
        await page.Locator("table").GetByText(propertyId).First.WaitForAsync();
    }

    [Fact]
    public async Task An_unknown_property_produces_a_clear_error()
    {
        var (page, _) = await LoginAsAdminOnHousekeepingAsync();

        await page.GetByRole(AriaRole.Button, new() { Name = "Nova limpeza" }).ClickAsync();
        var dialog = page.GetByRole(AriaRole.Dialog);
        await dialog.GetByLabel("ID do imóvel").FillAsync(Guid.NewGuid().ToString());
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Salvar" }).ClickAsync();

        (await AlertTextAsync(dialog)).Should().Contain("O imóvel informado não é conhecido ou não está ativo.");
        (await dialog.CountAsync()).Should().Be(1, "the dialog must remain open on a rejected submission, never silently close");
    }

    [Fact]
    public async Task Admin_completes_the_full_administrative_lifecycle()
    {
        var (page, token) = await LoginAsAdminOnHousekeepingAsync();
        var propertyId = await CreateActivePropertyViaApiAsync(page, token, "E2E-HK-LIFECYCLE-1", "E2E Housekeeping Lifecycle Property");
        var housekeeperEmail = $"e2e-housekeeper-{Guid.NewGuid():N}@e2e-playwright.test";
        var housekeeperUserId = await CreateHousekeeperUserViaApiAsync(page, token, "E2E Lifecycle Housekeeper", housekeeperEmail);
        var cleaningId = await CreateCleaningViaApiAsync(page, token, propertyId);
        await page.ReloadAsync();
        await FilterByPropertyIdAsync(page, propertyId);

        // .Last, not a "Pendente" text filter: this property also carries the
        // WaitUntilKnownToHousekeepingAsync readiness probe's own throwaway
        // Cancelled cleaning (always the OLDER of the two, sorted first by
        // the list's own oldest-first order) — the real cleaning is always
        // the last-created row for this property, and stays the SAME
        // physical row across every status transition below, unlike a
        // status-text filter which would go stale the moment the status
        // changes away from whatever it was filtered on.
        var row = page.Locator("tr", new PageLocatorOptions { HasText = propertyId }).Last;
        await row.GetByText("Pendente").WaitForAsync();

        // Assign — opens its own dialog, needs the real HOUSEKEEPER user's id.
        await row.GetByRole(AriaRole.Button, new() { Name = $"Ações para a limpeza de {propertyId}" }).ClickAsync();
        await page.GetByRole(AriaRole.Menuitem, new() { Name = "Atribuir camareira" }).ClickAsync();
        var assignDialog = page.GetByRole(AriaRole.Dialog);
        await assignDialog.GetByLabel("ID da camareira").FillAsync(housekeeperUserId);
        await assignDialog.GetByRole(AriaRole.Button, new() { Name = "Atribuir" }).ClickAsync();
        await page.GetByText("Camareira atribuída com sucesso.").WaitForAsync();
        await row.GetByText("Atribuída").WaitForAsync();

        // Start
        await row.GetByRole(AriaRole.Button, new() { Name = $"Ações para a limpeza de {propertyId}" }).ClickAsync();
        await page.GetByRole(AriaRole.Menuitem, new() { Name = "Iniciar", Exact = true }).ClickAsync();
        await page.GetByText("Limpeza iniciada com sucesso.").WaitForAsync();
        await row.GetByText("Em andamento").WaitForAsync();

        // StartInspection
        await row.GetByRole(AriaRole.Button, new() { Name = $"Ações para a limpeza de {propertyId}" }).ClickAsync();
        await page.GetByRole(AriaRole.Menuitem, new() { Name = "Iniciar inspeção" }).ClickAsync();
        await page.GetByText("Inspeção iniciada com sucesso.").WaitForAsync();
        await row.GetByText("Em inspeção").WaitForAsync();

        // Complete
        await row.GetByRole(AriaRole.Button, new() { Name = $"Ações para a limpeza de {propertyId}" }).ClickAsync();
        await page.GetByRole(AriaRole.Menuitem, new() { Name = "Concluir" }).ClickAsync();
        await page.GetByText("Limpeza concluída com sucesso.").WaitForAsync();
        await row.GetByText("Concluída").WaitForAsync();

        // Completed is terminal — no action menu at all.
        (await row.GetByRole(AriaRole.Button, new() { Name = $"Ações para a limpeza de {propertyId}" }).CountAsync())
            .Should().Be(0, "a Completed cleaning is terminal — no further action is offered");

        _ = cleaningId;
    }

    [Fact]
    public async Task Assigning_to_an_ineligible_user_shows_the_classified_error()
    {
        var (page, token) = await LoginAsAdminOnHousekeepingAsync();
        var propertyId = await CreateActivePropertyViaApiAsync(page, token, "E2E-HK-NOTELIGIBLE-1", "E2E Housekeeping Not Eligible Property");
        await CreateCleaningViaApiAsync(page, token, propertyId);
        await page.ReloadAsync();
        await FilterByPropertyIdAsync(page, propertyId);

        var row = page.Locator("tr", new PageLocatorOptions { HasText = propertyId }).Filter(new LocatorFilterOptions { HasText = "Pendente" });
        await row.WaitForAsync();
        await row.GetByRole(AriaRole.Button, new() { Name = $"Ações para a limpeza de {propertyId}" }).ClickAsync();
        await page.GetByRole(AriaRole.Menuitem, new() { Name = "Atribuir camareira" }).ClickAsync();

        var assignDialog = page.GetByRole(AriaRole.Dialog);
        // The ADMIN's own user id — a real, existing, active user of this
        // tenant, but never assigned the HOUSEKEEPER role — proving the
        // real backend eligibility check (IIdentityUserEligibilityReader),
        // never a UI-side assumption.
        var adminUserId = await GetOwnUserIdViaApiAsync(page, token);
        await assignDialog.GetByLabel("ID da camareira").FillAsync(adminUserId);
        await assignDialog.GetByRole(AriaRole.Button, new() { Name = "Atribuir" }).ClickAsync();

        (await AlertTextAsync(assignDialog)).Should().Contain("A camareira informada não é elegível");
        (await assignDialog.CountAsync()).Should().Be(1, "the dialog must remain open on a rejected submission, never silently close");
    }

    private async Task<string> GetOwnUserIdViaApiAsync(IPage page, string bearerToken)
    {
        var response = await page.Context.APIRequest.GetAsync(
            _fixture.ApiBaseUrl + "/api/v1/users/me",
            new APIRequestContextOptions { Headers = new Dictionary<string, string> { ["Authorization"] = bearerToken } });
        response.Ok.Should().BeTrue($"test-data setup via the real API must succeed (status {response.Status})");
        var body = await response.JsonAsync();
        return body!.Value.GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task Admin_cancels_a_pending_cleaning()
    {
        var (page, token) = await LoginAsAdminOnHousekeepingAsync();
        var propertyId = await CreateActivePropertyViaApiAsync(page, token, "E2E-HK-CANCEL-1", "E2E Housekeeping Cancel Property");
        await CreateCleaningViaApiAsync(page, token, propertyId);
        await page.ReloadAsync();
        await FilterByPropertyIdAsync(page, propertyId);

        // .Last, not a "Pendente" text filter — see the identical comment in
        // Admin_completes_the_full_administrative_lifecycle: this test's own
        // cancel action moves the real cleaning's status text to "Cancelada",
        // which would make a status-text-filtered locator go stale right
        // when the final assertion needs it most.
        var row = page.Locator("tr", new PageLocatorOptions { HasText = propertyId }).Last;
        await row.GetByText("Pendente").WaitForAsync();
        await row.GetByRole(AriaRole.Button, new() { Name = $"Ações para a limpeza de {propertyId}" }).ClickAsync();
        await page.GetByRole(AriaRole.Menuitem, new() { Name = "Cancelar limpeza" }).ClickAsync();

        await page.GetByRole(AriaRole.Heading, new() { Name = "Cancelar limpeza" }).WaitForAsync();
        await page.GetByRole(AriaRole.Dialog).GetByRole(AriaRole.Button, new() { Name = "Cancelar limpeza" }).ClickAsync();

        await page.GetByText("Limpeza cancelada com sucesso.").WaitForAsync();
        await row.GetByText("Cancelada").WaitForAsync();
        (await row.GetByRole(AriaRole.Button, new() { Name = $"Ações para a limpeza de {propertyId}" }).CountAsync())
            .Should().Be(0, "a Cancelled cleaning is terminal — no further action is offered");
    }

    [Fact]
    public async Task Filtering_by_status_narrows_the_list_to_matching_cleanings()
    {
        var (page, token) = await LoginAsAdminOnHousekeepingAsync();
        var pendingPropertyId = await CreateActivePropertyViaApiAsync(page, token, "E2E-HK-FILTER-PENDING-1", "E2E Housekeeping Filter Pending Property");
        await CreateCleaningViaApiAsync(page, token, pendingPropertyId);

        var cancelledPropertyId = await CreateActivePropertyViaApiAsync(page, token, "E2E-HK-FILTER-CANCELLED-1", "E2E Housekeeping Filter Cancelled Property");
        var cancelledCleaningId = await CreateCleaningViaApiAsync(page, token, cancelledPropertyId);
        var cancelResponse = await page.Context.APIRequest.PostAsync(
            _fixture.ApiBaseUrl + $"/api/v1/cleanings/{cancelledCleaningId}/cancel",
            new APIRequestContextOptions { Headers = new Dictionary<string, string> { ["Authorization"] = token } });
        cancelResponse.Ok.Should().BeTrue($"test-data setup via the real API must succeed (status {cancelResponse.Status})");

        await page.ReloadAsync();
        await page.GetByLabel("Status").ClickAsync();
        await page.GetByRole(AriaRole.Option, new() { Name = "Cancelada" }).ClickAsync();
        await page.GetByLabel("ID do imóvel").FillAsync(cancelledPropertyId);
        // Waits for the actual filtered GET /api/v1/cleanings response, not
        // just "some matching text is visible" — a plain WaitForAsync on the
        // table can be satisfied by content still on screen from BEFORE this
        // filter was applied (e.g. the initial unfiltered page-1 load), which
        // proves nothing about whether the filter itself actually took
        // effect. This is a real race that surfaced across two consecutive
        // full-suite runs, never on the Housekeeping-only runs that preceded
        // them — timing-sensitive, not a Housekeeping-specific defect.
        await page.RunAndWaitForResponseAsync(
            async () => await page.GetByRole(AriaRole.Button, new() { Name = "Filtrar" }).ClickAsync(),
            r => r.Url.Contains("/api/v1/cleanings") && r.Request.Method == "GET");

        var table = page.Locator("table");
        await table.GetByText(cancelledPropertyId).First.WaitForAsync();
        (await table.GetByText(pendingPropertyId).CountAsync()).Should().Be(0, "the propertyId+status filter must exclude every other property's cleanings entirely");

        // Second half of the check, the other way around: pendingPropertyId's
        // real cleaning stays Pending and must never surface under a Cancelada
        // filter. Filtered to exactly this property — WaitUntilKnownToHousekeepingAsync's
        // own readiness probe (CreateActivePropertyViaApiAsync) already created
        // and cancelled one throwaway cleaning for this same property, so
        // "no cleanings at all" is not the correct expectation here; the
        // correct one is "no *Pending* cleaning leaked into the Cancelada view".
        await page.GetByLabel("ID do imóvel").FillAsync(pendingPropertyId);
        await page.RunAndWaitForResponseAsync(
            async () => await page.GetByRole(AriaRole.Button, new() { Name = "Filtrar" }).ClickAsync(),
            r => r.Url.Contains("/api/v1/cleanings") && r.Request.Method == "GET");
        (await table.GetByText("Pendente").CountAsync()).Should().Be(0, "the status filter must exclude the real Pending cleaning even when scoped to its own property");
    }
}
