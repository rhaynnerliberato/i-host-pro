using System.Text.Json;
using FluentAssertions;
using Microsoft.Playwright;

namespace IHostPro.Web.Tests.E2E;

/// <summary>
/// Real-browser coverage for Fase 7, Incremento 2 (Dashboard &amp; Reporting
/// Foundation), Checkpoint 4 — the final E2E/homologation gate for the whole
/// Dashboard Overview API (Checkpoint 2) + Frontend Dashboard (Checkpoint 3).
/// Drives the real, unmodified <c>IHostPro.Web</c> against the real,
/// unmodified <c>IHostPro.Api</c>/<c>IHostPro.Worker</c> — see
/// <see cref="WebE2EFixture"/>, extended this checkpoint to provision
/// Dashboard's own schema/outbox/RabbitMQ topology/connection strings (the
/// same class of gap already found and fixed here for every prior new
/// Bounded Context). Mirrors <see cref="ScheduleAgendaE2ETests"/>'s pattern
/// exactly, including its bounded-polling idiom for eventually-consistent
/// projections — this suite additionally needs a composite predicate poll
/// (<see cref="WaitForDashboardOverviewAsync"/>) since the Overview API
/// returns AGGREGATE counts, never a list of individually-identifiable items
/// a later assertion could filter by id.
///
/// Count-sensitive scenarios (the full scenario, empty-tenant, cross-tenant)
/// each provision their OWN fresh tenant via
/// <see cref="WebE2EFixture.CreateAdditionalTenantWithAdminAsync"/> —
/// deliberately never the shared default tenant every other E2E suite in
/// this collection also creates Properties/Reservations/Cleanings against,
/// since the Overview API's own aggregate counts would otherwise be polluted
/// by any other test's data in the same tenant. Tests that only assert
/// request shape, navigation, or rendering (never an exact count) safely
/// reuse the shared default tenant, exactly like every other suite in this
/// collection already does.
/// </summary>
[Collection(WebE2EFixtureCollection.Name)]
public sealed class DashboardE2ETests
{
    private readonly WebE2EFixture _fixture;

    public DashboardE2ETests(WebE2EFixture fixture) => _fixture = fixture;

    // ---- Page/session setup -------------------------------------------

    private async Task<IPage> NewPageAsync(string? timezoneId = null)
    {
        var context = await _fixture.Browser.NewContextAsync(
            timezoneId is null ? null : new BrowserNewContextOptions { TimezoneId = timezoneId });
        return await context.NewPageAsync();
    }

    private async Task<(IPage Page, string BearerToken)> LoginAsync(
        string tenantSlug, string email, string password, string? timezoneId = null)
    {
        var page = await NewPageAsync(timezoneId);
        await page.GotoAsync(_fixture.WebBaseUrl + "/login");
        await page.GetByLabel("Empresa").FillAsync(tenantSlug);
        await page.GetByLabel("E-mail").FillAsync(email);
        await page.GetByLabel("Senha").FillAsync(password);

        var profileRequest = await page.RunAndWaitForRequestAsync(
            async () => await page.GetByRole(AriaRole.Button, new() { Name = "Entrar" }).ClickAsync(),
            req => req.Url.Contains("/api/v1/users/me") && req.Method == "GET");
        var bearerToken = await profileRequest.HeaderValueAsync("Authorization") ?? throw new InvalidOperationException("No Authorization header captured.");

        await page.WaitForURLAsync(_fixture.WebBaseUrl + "/");
        return (page, bearerToken);
    }

    /// <summary>Clicks the "Dashboard" nav link and waits for the first real GET /api/v1/dashboard/overview response — returns it for direct assertion on status/body.</summary>
    private async Task<IResponse> OpenDashboardAsync(IPage page)
    {
        var response = await page.RunAndWaitForResponseAsync(
            async () => await page.GetByRole(AriaRole.Link, new() { Name = "Dashboard" }).ClickAsync(),
            r => r.Url.Contains("/api/v1/dashboard/overview") && r.Request.Method == "GET");
        await page.WaitForURLAsync(_fixture.WebBaseUrl + "/dashboard");
        return response;
    }

    // ---- Seeding: Property/Reservation/Cleaning (direct API, mirrors ScheduleAgendaE2ETests) ----

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

        return propertyId;
    }

    private async Task DeactivatePropertyViaApiAsync(IPage page, string bearerToken, string propertyId)
    {
        var response = await page.Context.APIRequest.PostAsync(
            _fixture.ApiBaseUrl + $"/api/v1/properties/{propertyId}/deactivate",
            new APIRequestContextOptions { Headers = new Dictionary<string, string> { ["Authorization"] = bearerToken } });
        response.Ok.Should().BeTrue($"test-data setup via the real API must succeed (status {response.Status})");
    }

    private async Task ArchivePropertyViaApiAsync(IPage page, string bearerToken, string propertyId)
    {
        var response = await page.Context.APIRequest.PostAsync(
            _fixture.ApiBaseUrl + $"/api/v1/properties/{propertyId}/archive",
            new APIRequestContextOptions { Headers = new Dictionary<string, string> { ["Authorization"] = bearerToken } });
        response.Ok.Should().BeTrue($"test-data setup via the real API must succeed (status {response.Status})");
    }

    /// <summary>Mirrors ScheduleAgendaE2ETests.WaitUntilKnownToHousekeepingAsync exactly — a Cleaning cannot be created for a property until PropertyActivated has propagated (real RabbitMQ round trip) into Housekeeping's own local projection.</summary>
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

    private async Task<string> CreateReservationViaApiAsync(
        IPage page, string bearerToken, string propertyId, string guestName, DateTimeOffset checkInAt, DateTimeOffset checkOutAt)
    {
        var response = await page.Context.APIRequest.PostAsync(
            _fixture.ApiBaseUrl + "/api/v1/reservations",
            new APIRequestContextOptions
            {
                Headers = new Dictionary<string, string> { ["Authorization"] = bearerToken },
                DataObject = new
                {
                    propertyId,
                    guestName,
                    guestPhone = (string?)null,
                    checkInAt = checkInAt.ToString("O"),
                    checkOutAt = checkOutAt.ToString("O"),
                    guestCount = 2,
                },
            });
        response.Ok.Should().BeTrue($"test-data setup via the real API must succeed (status {response.Status}): {await response.TextAsync()}");
        var body = await response.JsonAsync();
        return body!.Value.GetProperty("id").GetString()!;
    }

    private async Task CancelReservationViaApiAsync(IPage page, string bearerToken, string reservationId)
    {
        var response = await page.Context.APIRequest.PostAsync(
            _fixture.ApiBaseUrl + $"/api/v1/reservations/{reservationId}/cancel",
            new APIRequestContextOptions { Headers = new Dictionary<string, string> { ["Authorization"] = bearerToken } });
        response.Ok.Should().BeTrue($"test-data setup via the real API must succeed (status {response.Status})");
    }

    private async Task<string> CreateCleaningViaApiAsync(IPage page, string bearerToken, string propertyId, DateTimeOffset? scheduledAtUtc = null)
    {
        var response = await page.Context.APIRequest.PostAsync(
            _fixture.ApiBaseUrl + "/api/v1/cleanings",
            new APIRequestContextOptions
            {
                Headers = new Dictionary<string, string> { ["Authorization"] = bearerToken },
                DataObject = new { propertyId, reservationId = (string?)null, scheduledAtUtc = scheduledAtUtc?.ToString("O") },
            });
        response.Ok.Should().BeTrue($"test-data setup via the real API must succeed (status {response.Status}): {await response.TextAsync()}");
        var body = await response.JsonAsync();
        return body!.Value.GetProperty("id").GetString()!;
    }

    private async Task<string> AssignCleaningViaApiAsync(IPage page, string bearerToken, string cleaningId, string housekeeperUserId)
    {
        var response = await page.Context.APIRequest.PostAsync(
            _fixture.ApiBaseUrl + $"/api/v1/cleanings/{cleaningId}/assign",
            new APIRequestContextOptions
            {
                Headers = new Dictionary<string, string> { ["Authorization"] = bearerToken },
                DataObject = new { housekeeperUserId },
            });
        response.Ok.Should().BeTrue($"test-data setup via the real API must succeed (status {response.Status})");
        return cleaningId;
    }

    private async Task CleaningActionViaApiAsync(IPage page, string bearerToken, string cleaningId, string action)
    {
        var response = await page.Context.APIRequest.PostAsync(
            _fixture.ApiBaseUrl + $"/api/v1/cleanings/{cleaningId}/{action}",
            new APIRequestContextOptions { Headers = new Dictionary<string, string> { ["Authorization"] = bearerToken } });
        response.Ok.Should().BeTrue($"test-data setup via the real API must succeed (status {response.Status}) for action '{action}'");
    }

    private async Task<(string Email, string Password, string UserId)> CreateHousekeeperViaApiAsync(IPage page, string bearerToken)
    {
        var email = $"dash-hk-{Guid.NewGuid():N}@e2e-playwright.test";
        const string password = "Correct-Horse-Battery-Staple-22!";
        var response = await page.Context.APIRequest.PostAsync(
            _fixture.ApiBaseUrl + "/api/v1/users",
            new APIRequestContextOptions
            {
                Headers = new Dictionary<string, string> { ["Authorization"] = bearerToken },
                DataObject = new { fullName = "E2E Dashboard Housekeeper", email, initialPassword = password, roleCode = "HOUSEKEEPER" },
            });
        response.Ok.Should().BeTrue($"test-data setup via the real API must succeed (status {response.Status})");
        var body = await response.JsonAsync();
        var userId = body!.Value.GetProperty("id").GetString()!;
        return (email, password, userId);
    }

    /// <summary>Registers an occurrence via the real housekeeper-portal endpoint (the only official flow for this — see MyCleaningsController) — never inserted directly into dashboard.* (mandate §5).</summary>
    private async Task RegisterOccurrenceAsHousekeeperAsync(
        string tenantSlug, string housekeeperEmail, string housekeeperPassword, string cleaningId, string type)
    {
        var (hkPage, hkToken) = await LoginAsync(tenantSlug, housekeeperEmail, housekeeperPassword);
        var response = await hkPage.Context.APIRequest.PostAsync(
            _fixture.ApiBaseUrl + $"/api/v1/my-cleanings/{cleaningId}/occurrences",
            new APIRequestContextOptions
            {
                Headers = new Dictionary<string, string> { ["Authorization"] = hkToken },
                DataObject = new { type, description = (string?)null },
            });
        response.Ok.Should().BeTrue($"test-data setup via the real API must succeed (status {response.Status}): {await response.TextAsync()}");
    }

    /// <summary>
    /// Bounded, composite-predicate poll of the real GET /api/v1/dashboard/overview
    /// — Dashboard's four projections are eventually consistent (real RabbitMQ
    /// round trips to the real Worker), and the Overview API returns AGGREGATE
    /// counts only, never a per-item id an assertion could filter by (unlike
    /// GET /api/v1/schedule) — so the wait must poll the composite shape
    /// itself until the caller's own predicate is satisfied. Never a fixed sleep.
    /// </summary>
    private async Task<JsonElement> WaitForDashboardOverviewAsync(
        IPage page, string bearerToken, DateTimeOffset from, DateTimeOffset to, Func<JsonElement, bool> predicate, int timeoutSeconds = 45)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(timeoutSeconds);
        JsonElement last = default;
        while (DateTime.UtcNow < deadline)
        {
            var response = await page.Context.APIRequest.GetAsync(
                _fixture.ApiBaseUrl + $"/api/v1/dashboard/overview?from={Uri.EscapeDataString(from.ToString("O"))}&to={Uri.EscapeDataString(to.ToString("O"))}",
                new APIRequestContextOptions { Headers = new Dictionary<string, string> { ["Authorization"] = bearerToken } });
            response.Ok.Should().BeTrue($"GET /api/v1/dashboard/overview must succeed (status {response.Status})");
            last = (await response.JsonAsync())!.Value;
            if (predicate(last))
                return last;
            await Task.Delay(TimeSpan.FromMilliseconds(400));
        }

        throw new TimeoutException($"Dashboard overview never matched the expected predicate within {timeoutSeconds}s. Last response: {last}");
    }

    /// <summary>
    /// label -&gt; value map for every `mat-card` on the currently-loaded Dashboard page. Returns the
    /// JS result as a JSON string and deserializes it manually — Playwright .NET's own
    /// <c>EvaluateAsync&lt;Dictionary&lt;string,string&gt;&gt;</c> was empirically confirmed (via a raw
    /// JSON-string round trip) to silently deserialize a genuinely non-empty JS object into an empty
    /// dictionary in this environment, so generic collection target types are avoided here.
    /// </summary>
    private static async Task<Dictionary<string, string>> ReadCardValuesAsync(IPage page)
    {
        var json = await page.EvaluateAsync<string>(@"() => {
            const result = {};
            document.querySelectorAll('mat-card').forEach(card => {
                const label = card.querySelector('.dashboard-card-label');
                const value = card.querySelector('.dashboard-card-value');
                if (label && value) result[label.textContent.trim()] = value.textContent.trim();
            });
            return JSON.stringify(result);
        }");
        return JsonSerializer.Deserialize<Dictionary<string, string>>(json)!;
    }

    /// <summary>
    /// Reads the two-column table immediately following the &lt;h3&gt; whose text matches
    /// headingText — [] if the section is currently showing its empty message instead of a table.
    /// Same JSON-string round trip as <see cref="ReadCardValuesAsync"/>, for the same reason.
    /// </summary>
    private static async Task<List<string[]>> ReadTableRowsAsync(IPage page, string headingText)
    {
        var json = await page.EvaluateAsync<string>(@"(heading) => {
            const h3s = [...document.querySelectorAll('h3')];
            const h3 = h3s.find(el => el.textContent.trim() === heading);
            if (!h3) return '[]';
            const rows = h3.parentElement.querySelectorAll('table tbody tr');
            return JSON.stringify([...rows].map(tr => [...tr.querySelectorAll('td')].map(td => td.textContent.trim())));
        }", headingText);
        return JsonSerializer.Deserialize<List<string[]>>(json)!;
    }

    // ---- 1/2/3: ADMIN/OPERATOR access, HOUSEKEEPER denial (mandate §8-10) ----

    [Fact]
    public async Task ADMIN_accesses_the_Dashboard_and_sees_a_real_200_response()
    {
        var (page, _) = await LoginAsync(WebE2EFixture.TenantSlugValue, WebE2EFixture.AdminEmail, WebE2EFixture.AdminPassword);

        var response = await OpenDashboardAsync(page);

        response.Status.Should().Be(200);
        await page.GetByRole(AriaRole.Heading, new() { Name = "Dashboard" }).WaitForAsync();
    }

    [Fact]
    public async Task OPERATOR_with_DASHBOARD_READ_accesses_the_Dashboard_and_metrics_load()
    {
        var (page, _) = await LoginAsync(WebE2EFixture.TenantSlugValue, WebE2EFixture.OperatorEmail, WebE2EFixture.OperatorPassword);

        var response = await OpenDashboardAsync(page);

        response.Status.Should().Be(200);
        await page.GetByRole(AriaRole.Heading, new() { Name = "Resumo do período" }).WaitForAsync();
    }

    /// <summary>HOUSEKEEPER holds no DASHBOARD:* permission at all (IdentityCatalogSeed) — the real seeded role that must be denied both the nav item and direct navigation.</summary>
    [Fact]
    public async Task HOUSEKEEPER_is_denied_the_Dashboard()
    {
        var (adminPage, adminToken) = await LoginAsync(WebE2EFixture.TenantSlugValue, WebE2EFixture.AdminEmail, WebE2EFixture.AdminPassword);
        var (hkEmail, hkPassword, _) = await CreateHousekeeperViaApiAsync(adminPage, adminToken);

        var (page, _) = await LoginAsync(WebE2EFixture.TenantSlugValue, hkEmail, hkPassword);

        (await page.GetByRole(AriaRole.Link, new() { Name = "Dashboard" }).CountAsync()).Should().Be(0, "HOUSEKEEPER holds no DASHBOARD:MANAGE/DASHBOARD:READ, so the nav item must not render at all");

        await page.GotoAsync(_fixture.WebBaseUrl + "/dashboard");
        await page.WaitForURLAsync(_fixture.WebBaseUrl + "/forbidden");
        page.Url.Should().Be(_fixture.WebBaseUrl + "/forbidden", "the route guard must deny direct navigation by real permission, not just hide the nav link");
    }

    // ---- 4: period presets — request shape only (shared tenant, no counts) ----

    [Fact]
    public async Task Period_Today_sends_local_day_boundaries_in_UTC_browser_timezone()
    {
        var (page, _) = await LoginAsync(WebE2EFixture.TenantSlugValue, WebE2EFixture.AdminEmail, WebE2EFixture.AdminPassword, timezoneId: "UTC");

        var response = await OpenDashboardAsync(page);

        var uri = new Uri(response.Url);
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        var from = DateTimeOffset.Parse(query["from"]!);
        var to = DateTimeOffset.Parse(query["to"]!);

        var expectedFrom = new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero);
        from.Should().Be(expectedFrom, "with the browser timezone explicitly UTC, 'Hoje' must send exactly today's UTC midnight, never shifted");
        to.Should().Be(expectedFrom.AddDays(1));
    }

    [Fact]
    public async Task Preset_Last7_and_Last30_send_the_correct_windows()
    {
        var (page, _) = await LoginAsync(WebE2EFixture.TenantSlugValue, WebE2EFixture.AdminEmail, WebE2EFixture.AdminPassword, timezoneId: "UTC");
        await OpenDashboardAsync(page);

        var last7Response = await page.RunAndWaitForResponseAsync(
            async () => await page.GetByRole(AriaRole.Radio, new() { Name = "Últimos 7 dias" }).ClickAsync(),
            r => r.Url.Contains("/api/v1/dashboard/overview") && r.Request.Method == "GET");
        var last7Query = System.Web.HttpUtility.ParseQueryString(new Uri(last7Response.Url).Query);
        var last7From = DateTimeOffset.Parse(last7Query["from"]!);
        var last7To = DateTimeOffset.Parse(last7Query["to"]!);
        (last7To - last7From).Should().Be(TimeSpan.FromDays(7));

        var last30Response = await page.RunAndWaitForResponseAsync(
            async () => await page.GetByRole(AriaRole.Radio, new() { Name = "Últimos 30 dias" }).ClickAsync(),
            r => r.Url.Contains("/api/v1/dashboard/overview") && r.Request.Method == "GET");
        var last30Query = System.Web.HttpUtility.ParseQueryString(new Uri(last30Response.Url).Query);
        var last30From = DateTimeOffset.Parse(last30Query["from"]!);
        var last30To = DateTimeOffset.Parse(last30Query["to"]!);
        (last30To - last30From).Should().Be(TimeSpan.FromDays(30));
        last30To.Should().Be(last7To, "both presets must share the same exclusive upper boundary (start of tomorrow)");
    }

    // ---- 18/19: custom range — valid + invalid (shared tenant, no counts) ----

    [Fact]
    public async Task Custom_range_valid_sends_the_correct_bounds_and_invalid_input_is_rejected_client_side()
    {
        var (page, _) = await LoginAsync(WebE2EFixture.TenantSlugValue, WebE2EFixture.AdminEmail, WebE2EFixture.AdminPassword, timezoneId: "UTC");
        await OpenDashboardAsync(page);

        await page.GetByRole(AriaRole.Radio, new() { Name = "Período personalizado" }).ClickAsync();
        var today = DateTime.UtcNow.Date;
        var fromDate = today.AddDays(-10);

        // Invalid: to < from — must show validation, never request, and leave the prior period untouched.
        // Exact match is required: Playwright's default GetByLabel substring-matches, and "De" is
        // itself a substring of the "Detalhes" section's accessible name (aria-labelledby heading).
        await page.GetByLabel("De", new() { Exact = true }).FillAsync(today.ToString("yyyy-MM-dd"));
        await page.GetByLabel("Até", new() { Exact = true }).FillAsync(fromDate.ToString("yyyy-MM-dd"));
        var requestsBeforeInvalid = 0;
        page.Request += (_, req) => { if (req.Url.Contains("/api/v1/dashboard/overview")) requestsBeforeInvalid++; };
        await page.GetByRole(AriaRole.Button, new() { Name = "Aplicar" }).ClickAsync();
        await page.GetByText("Informe um intervalo válido").WaitForAsync();
        requestsBeforeInvalid.Should().Be(0, "an invalid custom range must never reach the backend");

        // Valid: apply a real 11-day range and confirm the real request + label.
        await page.GetByLabel("De", new() { Exact = true }).FillAsync(fromDate.ToString("yyyy-MM-dd"));
        await page.GetByLabel("Até", new() { Exact = true }).FillAsync(today.ToString("yyyy-MM-dd"));
        var validResponse = await page.RunAndWaitForResponseAsync(
            async () => await page.GetByRole(AriaRole.Button, new() { Name = "Aplicar" }).ClickAsync(),
            r => r.Url.Contains("/api/v1/dashboard/overview") && r.Request.Method == "GET");
        var query = System.Web.HttpUtility.ParseQueryString(new Uri(validResponse.Url).Query);
        var from = DateTimeOffset.Parse(query["from"]!);
        var to = DateTimeOffset.Parse(query["to"]!);
        (to - from).Should().Be(TimeSpan.FromDays(11), "both bounds are inclusive calendar days");
    }

    // ---- 21: manual refresh (shared tenant, request shape only) ----

    [Fact]
    public async Task Manual_refresh_requests_the_same_period_again_without_a_full_page_reload()
    {
        var (page, _) = await LoginAsync(WebE2EFixture.TenantSlugValue, WebE2EFixture.AdminEmail, WebE2EFixture.AdminPassword);
        var initialResponse = await OpenDashboardAsync(page);
        var initialQuery = System.Web.HttpUtility.ParseQueryString(new Uri(initialResponse.Url).Query);

        var refreshResponse = await page.RunAndWaitForResponseAsync(
            async () => await page.GetByRole(AriaRole.Button, new() { Name = "Atualizar" }).ClickAsync(),
            r => r.Url.Contains("/api/v1/dashboard/overview") && r.Request.Method == "GET");

        var refreshQuery = System.Web.HttpUtility.ParseQueryString(new Uri(refreshResponse.Url).Query);
        refreshQuery["from"].Should().Be(initialQuery["from"]);
        refreshQuery["to"].Should().Be(initialQuery["to"]);
        // A manual refresh must never trigger a full navigation/reload — the heading is still present without re-navigating.
        await page.GetByRole(AriaRole.Heading, new() { Name = "Dashboard" }).WaitForAsync();
    }

    // ---- 26/27: responsive + accessibility smoke (shared tenant, rendering only) ----

    [Fact]
    public async Task Dashboard_is_responsive_at_375px_and_desktop()
    {
        var context = await _fixture.Browser.NewContextAsync(new BrowserNewContextOptions { ViewportSize = new ViewportSize { Width = 375, Height = 812 } });
        var page = await context.NewPageAsync();
        await page.GotoAsync(_fixture.WebBaseUrl + "/login");
        await page.GetByLabel("Empresa").FillAsync(WebE2EFixture.TenantSlugValue);
        await page.GetByLabel("E-mail").FillAsync(WebE2EFixture.AdminEmail);
        await page.GetByLabel("Senha").FillAsync(WebE2EFixture.AdminPassword);
        await page.GetByRole(AriaRole.Button, new() { Name = "Entrar" }).ClickAsync();
        await page.WaitForURLAsync(_fixture.WebBaseUrl + "/");
        await OpenDashboardAsync(page);

        var overflows = await page.EvaluateAsync<bool>("document.documentElement.scrollWidth > window.innerWidth + 1");
        overflows.Should().BeFalse("the Dashboard must never introduce horizontal overflow at 375px");
        await page.GetByRole(AriaRole.Button, new() { Name = "Atualizar" }).WaitForAsync();
        (await page.GetByRole(AriaRole.Radio, new() { Name = "Hoje" }).IsVisibleAsync()).Should().BeTrue("the period selector must remain usable at 375px");

        await page.SetViewportSizeAsync(1280, 800);
        var stillOverflows = await page.EvaluateAsync<bool>("document.documentElement.scrollWidth > window.innerWidth + 1");
        stillOverflows.Should().BeFalse("the Dashboard must never introduce horizontal overflow at desktop width either");
    }

    [Fact]
    public async Task Dashboard_accessibility_smoke()
    {
        // Own fresh tenant with one real reservation: the shared default tenant's StatusCounts/
        // OccurrenceByType data depends on which other suites in the collection have already run
        // (xUnit gives no cross-class ordering guarantee), so asserting real <table> semantics
        // needs deterministic data — otherwise the section may legitimately render its empty-state
        // <p> instead of a <table>, which is not an accessibility defect.
        var (_, slug, email, password) = await _fixture.CreateAdditionalTenantWithAdminAsync();
        var (page, token) = await LoginAsync(slug, email, password, timezoneId: "UTC");

        var property = await CreateActivePropertyViaApiAsync(page, token, "E2E-DASH-A11Y", "Dashboard Accessibility Property");
        var now = DateTimeOffset.UtcNow;
        await CreateReservationViaApiAsync(page, token, property, "Accessibility Guest", now, now.AddDays(2));

        var todayStart = new DateTimeOffset(now.Date, TimeSpan.Zero);
        var tomorrowStart = todayStart.AddDays(1);
        await WaitForDashboardOverviewAsync(page, token, todayStart, tomorrowStart,
            json => json.GetProperty("reservations").GetProperty("statusCounts").GetArrayLength() > 0);

        await OpenDashboardAsync(page);

        await page.GetByRole(AriaRole.Heading, new() { Name = "Dashboard" }).WaitForAsync();
        (await page.GetByRole(AriaRole.Button, new() { Name = "Atualizar" }).CountAsync()).Should().BeGreaterThan(0, "the refresh control must be reachable by role+accessible name");
        (await page.GetByRole(AriaRole.Radio, new() { Name = "Hoje" }).CountAsync()).Should().BeGreaterThan(0, "period controls must be reachable by role+accessible name");

        var tableHeaderCellCount = await page.Locator("table th[scope='col']").CountAsync();
        tableHeaderCellCount.Should().BeGreaterThan(0, "detail tables must expose real semantic column headers, never purely visual ones");
    }

    // ---- 24: empty tenant (own fresh tenant) ----

    [Fact]
    public async Task Empty_tenant_dashboard_returns_200_with_zeros_and_no_error()
    {
        var (_, slug, email, password) = await _fixture.CreateAdditionalTenantWithAdminAsync();

        var (page, _) = await LoginAsync(slug, email, password, timezoneId: "UTC");

        var response = await OpenDashboardAsync(page);

        response.Status.Should().Be(200, "an overview with zero operational data must still be 200, never 404");
        // The network response resolving does not guarantee Angular has already rendered the
        // cards from it — wait for the DOM to actually reflect the response before reading it.
        await page.Locator("mat-card").First.WaitForAsync();
        var cards = await ReadCardValuesAsync(page);
        cards.Should().NotBeEmpty();
        cards.Values.Should().OnlyContain(v => v == "0", "a brand-new tenant with no data must render every card as zero, never an error");
        await page.GetByText("Nenhum dado neste período.").First.WaitForAsync();
    }

    // ---- 7: cross-tenant isolation (two fresh tenants, distinct real data) ----

    [Fact]
    public async Task Cross_tenant_isolation_dashboard_never_shows_another_tenants_numbers()
    {
        var (_, slugA, emailA, passwordA) = await _fixture.CreateAdditionalTenantWithAdminAsync();
        var (_, slugB, emailB, passwordB) = await _fixture.CreateAdditionalTenantWithAdminAsync();

        var (pageA, tokenA) = await LoginAsync(slugA, emailA, passwordA, timezoneId: "UTC");
        var propertyA1 = await CreateActivePropertyViaApiAsync(pageA, tokenA, "E2E-DASH-XT-A1", "Tenant A Property 1");
        var propertyA2 = await CreateActivePropertyViaApiAsync(pageA, tokenA, "E2E-DASH-XT-A2", "Tenant A Property 2");
        var now = DateTimeOffset.UtcNow;
        await CreateReservationViaApiAsync(pageA, tokenA, propertyA1, "Tenant A Guest 1", now, now.AddDays(3));
        await CreateReservationViaApiAsync(pageA, tokenA, propertyA2, "Tenant A Guest 2", now.AddDays(-2), now.AddDays(1));

        var (pageB, tokenB) = await LoginAsync(slugB, emailB, passwordB, timezoneId: "UTC");
        var propertyB1 = await CreateActivePropertyViaApiAsync(pageB, tokenB, "E2E-DASH-XT-B1", "Tenant B Property 1");
        await CreateReservationViaApiAsync(pageB, tokenB, propertyB1, "Tenant B Guest 1", now, now.AddDays(5));

        var todayStart = new DateTimeOffset(now.Date, TimeSpan.Zero);
        var tomorrowStart = todayStart.AddDays(1);

        await WaitForDashboardOverviewAsync(pageA, tokenA, todayStart, tomorrowStart,
            json => json.GetProperty("properties").GetProperty("active").GetInt32() == 2);
        await WaitForDashboardOverviewAsync(pageB, tokenB, todayStart, tomorrowStart,
            json => json.GetProperty("properties").GetProperty("active").GetInt32() == 1);

        var overviewA = await pageA.Context.APIRequest.GetAsync(
            _fixture.ApiBaseUrl + $"/api/v1/dashboard/overview?from={Uri.EscapeDataString(todayStart.ToString("O"))}&to={Uri.EscapeDataString(tomorrowStart.ToString("O"))}",
            new APIRequestContextOptions { Headers = new Dictionary<string, string> { ["Authorization"] = tokenA } });
        var jsonA = (await overviewA.JsonAsync())!.Value;
        jsonA.GetProperty("properties").GetProperty("active").GetInt32().Should().Be(2, "Tenant A must see exactly its own 2 active properties, never Tenant B's");
        jsonA.GetProperty("reservations").GetProperty("checkInsInPeriod").GetInt32().Should().Be(1, "Tenant A must see exactly its own check-ins, never summed with Tenant B's");

        var overviewB = await pageB.Context.APIRequest.GetAsync(
            _fixture.ApiBaseUrl + $"/api/v1/dashboard/overview?from={Uri.EscapeDataString(todayStart.ToString("O"))}&to={Uri.EscapeDataString(tomorrowStart.ToString("O"))}",
            new APIRequestContextOptions { Headers = new Dictionary<string, string> { ["Authorization"] = tokenB } });
        var jsonB = (await overviewB.JsonAsync())!.Value;
        jsonB.GetProperty("properties").GetProperty("active").GetInt32().Should().Be(1, "Tenant B must see exactly its own 1 active property, never Tenant A's 2");
        jsonB.GetProperty("reservations").GetProperty("checkInsInPeriod").GetInt32().Should().Be(1);

        // And in the real UI, never just a zero-vs-zero comparison (mandate §7): both tenants have
        // genuinely different real numbers, and each must see only its own. Neither page has ever
        // navigated to /dashboard yet (LoginAsync lands on "/", and everything above used direct
        // API calls) — navigate there for real instead of reloading a page that was never on it.
        await OpenDashboardAsync(pageA);
        await pageA.Locator("mat-card").First.WaitForAsync();
        var cardsA = await ReadCardValuesAsync(pageA);
        cardsA["Imóveis ativos"].Should().Be("2");

        await OpenDashboardAsync(pageB);
        await pageB.Locator("mat-card").First.WaitForAsync();
        var cardsB = await ReadCardValuesAsync(pageB);
        cardsB["Imóveis ativos"].Should().Be("1");
    }

    // ---- 11/12/13/14: the full real scenario — period + current-state metrics, StatusCounts, Occurrence ByType (own fresh tenant) ----

    [Fact]
    public async Task Dashboard_reflects_real_period_and_current_state_metrics_from_the_full_scenario()
    {
        var (_, slug, adminEmail, adminPassword) = await _fixture.CreateAdditionalTenantWithAdminAsync();
        var (page, token) = await LoginAsync(slug, adminEmail, adminPassword, timezoneId: "UTC");
        var now = DateTimeOffset.UtcNow;
        var todayStart = new DateTimeOffset(now.Date, TimeSpan.Zero);
        var tomorrowStart = todayStart.AddDays(1);

        // ---- Reservations: future / check-in today / check-out today / cancelled ----
        var propertyFuture = await CreateActivePropertyViaApiAsync(page, token, "E2E-DASH-FUT", "Dashboard Future Res Property");
        var propertyCheckIn = await CreateActivePropertyViaApiAsync(page, token, "E2E-DASH-CI", "Dashboard Check-in Property");
        var propertyCheckOut = await CreateActivePropertyViaApiAsync(page, token, "E2E-DASH-CO", "Dashboard Check-out Property");
        var propertyCancelled = await CreateActivePropertyViaApiAsync(page, token, "E2E-DASH-CANC", "Dashboard Cancelled Res Property");

        await CreateReservationViaApiAsync(page, token, propertyFuture, "Future Guest", now.AddDays(3), now.AddDays(6));
        await CreateReservationViaApiAsync(page, token, propertyCheckIn, "Check-in Guest", now, now.AddDays(3));
        await CreateReservationViaApiAsync(page, token, propertyCheckOut, "Check-out Guest", now.AddDays(-2), now);
        var cancelledReservationId = await CreateReservationViaApiAsync(page, token, propertyCancelled, "Cancelled Guest", now.AddDays(1), now.AddDays(4));
        await CancelReservationViaApiAsync(page, token, cancelledReservationId);

        // ---- Properties: active (the four above) / inactive / archived ----
        var propertyInactive = await CreateActivePropertyViaApiAsync(page, token, "E2E-DASH-INACT", "Dashboard Inactive Property");
        await DeactivatePropertyViaApiAsync(page, token, propertyInactive);
        var propertyArchived = await CreateActivePropertyViaApiAsync(page, token, "E2E-DASH-ARCH", "Dashboard Archived Property");
        await DeactivatePropertyViaApiAsync(page, token, propertyArchived);
        await ArchivePropertyViaApiAsync(page, token, propertyArchived);

        // ---- Cleanings: pending / started(in-progress) / waiting-help / completed / cancelled / delayed / interrupted ----
        var propertyClean = await CreateActivePropertyViaApiAsync(page, token, "E2E-DASH-CLEAN", "Dashboard Cleaning Property");
        await WaitUntilKnownToHousekeepingAsync(page, token, propertyClean);
        var (hkEmail, hkPassword, hkUserId) = await CreateHousekeeperViaApiAsync(page, token);

        var cleaningPending = await CreateCleaningViaApiAsync(page, token, propertyClean);
        _ = cleaningPending;

        var cleaningStarted = await AssignCleaningViaApiAsync(page, token, await CreateCleaningViaApiAsync(page, token, propertyClean), hkUserId);
        await CleaningActionViaApiAsync(page, token, cleaningStarted, "start");

        var cleaningWaitingHelp = await AssignCleaningViaApiAsync(page, token, await CreateCleaningViaApiAsync(page, token, propertyClean), hkUserId);
        await CleaningActionViaApiAsync(page, token, cleaningWaitingHelp, "start");
        await CleaningActionViaApiAsync(page, token, cleaningWaitingHelp, "waiting-help");

        var cleaningCompleted = await AssignCleaningViaApiAsync(page, token, await CreateCleaningViaApiAsync(page, token, propertyClean), hkUserId);
        await CleaningActionViaApiAsync(page, token, cleaningCompleted, "start");
        await CleaningActionViaApiAsync(page, token, cleaningCompleted, "start-inspection");
        await CleaningActionViaApiAsync(page, token, cleaningCompleted, "complete");

        var cleaningCancelled = await CreateCleaningViaApiAsync(page, token, propertyClean);
        await CleaningActionViaApiAsync(page, token, cleaningCancelled, "cancel");

        var cleaningDelayed = await CreateCleaningViaApiAsync(page, token, propertyClean, scheduledAtUtc: now.AddDays(-1));
        _ = cleaningDelayed;

        var cleaningInterrupted = await AssignCleaningViaApiAsync(page, token, await CreateCleaningViaApiAsync(page, token, propertyClean), hkUserId);
        await CleaningActionViaApiAsync(page, token, cleaningInterrupted, "start");
        await CleaningActionViaApiAsync(page, token, cleaningInterrupted, "interrupt");

        // ---- Occurrences: two distinct types, registered by the real assigned housekeeper ----
        await RegisterOccurrenceAsHousekeeperAsync(slug, hkEmail, hkPassword, cleaningStarted, "Damage");
        await RegisterOccurrenceAsHousekeeperAsync(slug, hkEmail, hkPassword, cleaningStarted, "Noise");

        // ---- Wait for every real RabbitMQ-propagated projection to settle, via one composite predicate ----
        await WaitForDashboardOverviewAsync(page, token, todayStart, tomorrowStart, json =>
            json.GetProperty("reservations").GetProperty("checkInsInPeriod").GetInt32() == 1 &&
            json.GetProperty("reservations").GetProperty("checkOutsInPeriod").GetInt32() == 1 &&
            json.GetProperty("reservations").GetProperty("cancelledInPeriod").GetInt32() == 1 &&
            json.GetProperty("reservations").GetProperty("futureReservations").GetInt32() == 1 &&
            json.GetProperty("housekeeping").GetProperty("completedInPeriod").GetInt32() == 1 &&
            // 2, not 1: WaitUntilKnownToHousekeepingAsync's own probe cleaning (created+cancelled
            // "now" to detect PropertyActivated propagation) also lands in today's cancelled count.
            json.GetProperty("housekeeping").GetProperty("cancelledInPeriod").GetInt32() == 2 &&
            json.GetProperty("housekeeping").GetProperty("waitingHelp").GetInt32() == 1 &&
            json.GetProperty("housekeeping").GetProperty("delayed").GetInt32() == 1 &&
            json.GetProperty("housekeeping").GetProperty("interrupted").GetInt32() == 1 &&
            json.GetProperty("occurrences").GetProperty("totalInPeriod").GetInt32() == 2 &&
            json.GetProperty("properties").GetProperty("active").GetInt32() == 5 &&
            json.GetProperty("properties").GetProperty("inactive").GetInt32() == 1 &&
            json.GetProperty("properties").GetProperty("archived").GetInt32() == 1);

        // This page has never navigated to /dashboard yet (LoginAsync lands on "/", and the whole
        // scenario above was seeded via direct API calls) — navigate there for real rather than
        // reloading a page that was never on it.
        await OpenDashboardAsync(page);
        await page.GetByRole(AriaRole.Heading, new() { Name = "Resumo do período" }).WaitForAsync();
        var cards = await ReadCardValuesAsync(page);

        // Period metrics
        cards["Check-ins no período"].Should().Be("1");
        cards["Check-outs no período"].Should().Be("1");
        cards["Reservas canceladas no período"].Should().Be("1");
        cards["Faxinas concluídas no período"].Should().Be("1");
        cards["Faxinas canceladas no período"].Should().Be("2", "the explicit cleaningCancelled plus WaitUntilKnownToHousekeepingAsync's own probe cleaning cancellation");
        cards["Ocorrências no período"].Should().Be("2");

        // Current-state metrics
        cards["Reservas futuras"].Should().Be("1");
        cards["Faxinas pendentes"].Should().Be("2", "Pending bucket = Pending+Assigned: the plain Pending cleaning, plus the Delayed one (still Pending)");
        cards["Faxinas em andamento"].Should().Be("2", "InProgress bucket = InTransit/Started/InInspection/WaitingHelp/WaitingMaterials: the Started cleaning plus the WaitingHelp one");
        cards["Faxinas interrompidas"].Should().Be("1");
        cards["Faxinas atrasadas"].Should().Be("1");
        cards["Aguardando ajuda"].Should().Be("1");
        cards["Aguardando material"].Should().Be("0");

        // Properties
        cards["Imóveis ativos"].Should().Be("5");
        cards["Imóveis inativos"].Should().Be("1");
        cards["Imóveis arquivados"].Should().Be("1");

        // StatusCounts table — real labels, real counts
        var statusRows = await ReadTableRowsAsync(page, "Reservas por status");
        var statusByLabel = statusRows.ToDictionary(r => r[0], r => r[1]);
        statusByLabel.Should().ContainKey("Confirmada");
        statusByLabel.Should().ContainKey("Cancelada");
        statusByLabel["Cancelada"].Should().Be("1");
        statusByLabel["Confirmada"].Should().Be("3", "the three non-cancelled reservations (future/check-in/check-out) are all still Confirmed");

        // Occurrence ByType table — two distinct real types, real translated labels, never a Description
        var occurrenceRows = await ReadTableRowsAsync(page, "Ocorrências por tipo");
        var occurrenceByLabel = occurrenceRows.ToDictionary(r => r[0], r => r[1]);
        occurrenceByLabel.Should().ContainKey("Dano");
        occurrenceByLabel.Should().ContainKey("Barulho");
        occurrenceByLabel["Dano"].Should().Be("1");
        occurrenceByLabel["Barulho"].Should().Be("1");
        var bodyText = await page.InnerTextAsync("body");
        bodyText.Should().NotContain("Description", "the Overview API/UI must never surface an occurrence Description");

        // Last updated renders a valid, non-empty, localized timestamp
        var lastUpdatedText = await page.Locator(".dashboard-last-updated").InnerTextAsync();
        lastUpdatedText.Should().NotBeNullOrWhiteSpace();
        lastUpdatedText.Should().Contain("Última atualização");
    }
}
