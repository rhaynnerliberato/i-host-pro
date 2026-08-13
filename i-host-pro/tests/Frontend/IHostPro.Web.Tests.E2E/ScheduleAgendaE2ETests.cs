using System.Text.Json;
using FluentAssertions;
using Microsoft.Playwright;

namespace IHostPro.Web.Tests.E2E;

/// <summary>
/// Real-browser coverage for Fase 7, Incremento 1 (Agenda Foundation),
/// Checkpoint 3 — the formal Playwright suite for the read-only Agenda
/// (<c>/schedule</c>, FullCalendar-backed, <c>GET /api/v1/schedule</c>).
/// Drives the real, unmodified <c>IHostPro.Web</c> against the real,
/// unmodified <c>IHostPro.Api</c>/<c>IHostPro.Worker</c> — see
/// <see cref="WebE2EFixture"/>. Every test creates its own throwaway
/// Property/Reservation/Cleaning directly through the real API — using a
/// real bearer token captured off a real network request — then drives only
/// the Agenda UI itself through the real Angular app. Mirrors
/// <see cref="HousekeepingE2ETests"/>'s and <see cref="ReservationsE2ETests"/>'s
/// pattern exactly, including <c>WaitUntilKnownToHousekeepingAsync</c>'s
/// bounded-polling idiom for Housekeeping's own asynchronous property
/// projection — this suite additionally needs the SAME kind of bounded poll
/// for Reservations' own asynchronous <c>CleaningScheduleProjection</c> (a
/// Cleaning created through Housekeeping's API is not immediately visible on
/// <c>GET /api/v1/schedule</c> — it propagates over a real RabbitMQ round
/// trip to the real Worker, exactly as documented in the Fase 7 homologação
/// document's Checkpoint 1 gates).
///
/// Does NOT test drag/drop, resize, or any Agenda editing — this increment's
/// Agenda is read-only by design (Checkpoint 2 approval). Does NOT test
/// Dashboard — out of scope for this entire Fase.
/// </summary>
[Collection(WebE2EFixtureCollection.Name)]
public sealed class ScheduleAgendaE2ETests
{
    private readonly WebE2EFixture _fixture;

    public ScheduleAgendaE2ETests(WebE2EFixture fixture) => _fixture = fixture;

    // ---- Page/session setup -------------------------------------------

    private async Task<IPage> NewPageAsync(string? timezoneId = null)
    {
        var context = await _fixture.Browser.NewContextAsync(
            timezoneId is null ? null : new BrowserNewContextOptions { TimezoneId = timezoneId });
        return await context.NewPageAsync();
    }

    /// <summary>Logs in as ADMIN and returns the page positioned on /schedule, plus the real bearer token the app itself used for GET /api/v1/users/me — for API-level test-data setup only, never for driving the UI.</summary>
    private async Task<(IPage Page, string BearerToken)> LoginAsAdminOnScheduleAsync(string? timezoneId = null)
    {
        var page = await NewPageAsync(timezoneId);
        await page.GotoAsync(_fixture.WebBaseUrl + "/login");
        await page.GetByLabel("Empresa").FillAsync(WebE2EFixture.TenantSlugValue);
        await page.GetByLabel("E-mail").FillAsync(WebE2EFixture.AdminEmail);
        await page.GetByLabel("Senha").FillAsync(WebE2EFixture.AdminPassword);

        var profileRequest = await page.RunAndWaitForRequestAsync(
            async () => await page.GetByRole(AriaRole.Button, new() { Name = "Entrar" }).ClickAsync(),
            req => req.Url.Contains("/api/v1/users/me") && req.Method == "GET");
        var bearerToken = await profileRequest.HeaderValueAsync("Authorization") ?? throw new InvalidOperationException("No Authorization header captured.");

        await page.WaitForURLAsync(_fixture.WebBaseUrl + "/");
        await page.GetByRole(AriaRole.Link, new() { Name = "Agenda" }).ClickAsync();
        await page.WaitForURLAsync(_fixture.WebBaseUrl + "/schedule");

        return (page, bearerToken);
    }

    /// <summary>Logs in as OPERATOR and returns the page positioned on /schedule, plus its real bearer token.</summary>
    private async Task<(IPage Page, string BearerToken)> LoginAsOperatorOnScheduleAsync()
    {
        var page = await NewPageAsync();
        await page.GotoAsync(_fixture.WebBaseUrl + "/login");
        await page.GetByLabel("Empresa").FillAsync(WebE2EFixture.TenantSlugValue);
        await page.GetByLabel("E-mail").FillAsync(WebE2EFixture.OperatorEmail);
        await page.GetByLabel("Senha").FillAsync(WebE2EFixture.OperatorPassword);

        var profileRequest = await page.RunAndWaitForRequestAsync(
            async () => await page.GetByRole(AriaRole.Button, new() { Name = "Entrar" }).ClickAsync(),
            req => req.Url.Contains("/api/v1/users/me") && req.Method == "GET");
        var bearerToken = await profileRequest.HeaderValueAsync("Authorization") ?? throw new InvalidOperationException("No Authorization header captured.");

        await page.WaitForURLAsync(_fixture.WebBaseUrl + "/");
        await page.GetByRole(AriaRole.Link, new() { Name = "Agenda" }).ClickAsync();
        await page.WaitForURLAsync(_fixture.WebBaseUrl + "/schedule");

        return (page, bearerToken);
    }

    // ---- Seeding: Property/Reservation/Cleaning (direct API, mirrors HousekeepingE2ETests/ReservationsE2ETests) ----

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

    /// <summary>Mirrors HousekeepingE2ETests.WaitUntilKnownToHousekeepingAsync exactly — a Cleaning cannot be created for a property until PropertyActivated has propagated (real RabbitMQ round trip) into Housekeeping's own local projection. Only needed before creating a Cleaning; Reservations validates properties synchronously (ADR-014) and needs no such wait.</summary>
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
        IPage page, string bearerToken, string propertyId, string guestName, string checkInAtIso, string checkOutAtIso)
    {
        var response = await page.Context.APIRequest.PostAsync(
            _fixture.ApiBaseUrl + "/api/v1/reservations",
            new APIRequestContextOptions
            {
                Headers = new Dictionary<string, string> { ["Authorization"] = bearerToken },
                DataObject = new { propertyId, guestName, guestPhone = (string?)null, checkInAt = checkInAtIso, checkOutAt = checkOutAtIso, guestCount = 2 },
            });
        response.Ok.Should().BeTrue($"test-data setup via the real API must succeed (status {response.Status})");
        var body = await response.JsonAsync();
        return body!.Value.GetProperty("id").GetString()!;
    }

    /// <summary>Creates a Cleaning WITH a real scheduledAtUtc directly through the real API (the Housekeeping admin UI's own "Nova limpeza" dialog does not expose this field, but the official command/API contract accepts it — CreateCleaningCommand.ScheduledAtUtc). Returns its id.</summary>
    private async Task<string> CreateScheduledCleaningViaApiAsync(IPage page, string bearerToken, string propertyId, string scheduledAtUtcIso)
    {
        var response = await page.Context.APIRequest.PostAsync(
            _fixture.ApiBaseUrl + "/api/v1/cleanings",
            new APIRequestContextOptions
            {
                Headers = new Dictionary<string, string> { ["Authorization"] = bearerToken },
                DataObject = new { propertyId, reservationId = (string?)null, scheduledAtUtc = scheduledAtUtcIso },
            });
        response.Ok.Should().BeTrue($"test-data setup via the real API must succeed (status {response.Status})");
        var body = await response.JsonAsync();
        return body!.Value.GetProperty("id").GetString()!;
    }

    /// <summary>
    /// Bounded poll of the real GET /api/v1/schedule for a specific
    /// sourceReferenceId — Cleaning propagation from Housekeeping into
    /// Reservations' own CleaningScheduleProjection is asynchronous (real
    /// RabbitMQ round trip to the real Worker), exactly like
    /// WaitUntilKnownToHousekeepingAsync above. Never a fixed sleep.
    /// </summary>
    private async Task WaitUntilVisibleInScheduleAsync(IPage page, string bearerToken, string fromIso, string toIso, string sourceReferenceId)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            var response = await page.Context.APIRequest.GetAsync(
                _fixture.ApiBaseUrl + $"/api/v1/schedule?from={Uri.EscapeDataString(fromIso)}&to={Uri.EscapeDataString(toIso)}",
                new APIRequestContextOptions { Headers = new Dictionary<string, string> { ["Authorization"] = bearerToken } });
            response.Ok.Should().BeTrue($"GET /api/v1/schedule must succeed (status {response.Status})");
            var items = await response.JsonAsync();
            if (items!.Value.EnumerateArray().Any(i => i.GetProperty("sourceReferenceId").GetString() == sourceReferenceId))
                return;
            await Task.Delay(TimeSpan.FromMilliseconds(300));
        }

        throw new TimeoutException($"Item {sourceReferenceId} never appeared in GET /api/v1/schedule within 20s.");
    }

    // ---- 1/2/3: ADMIN/OPERATOR access, permission denial ----------------

    [Fact]
    public async Task ADMIN_accesses_the_Agenda()
    {
        var (page, _) = await LoginAsAdminOnScheduleAsync();

        page.Url.Should().Be(_fixture.WebBaseUrl + "/schedule");
        await page.GetByText("Agenda").First.WaitForAsync();
    }

    [Fact]
    public async Task OPERATOR_accesses_the_Agenda()
    {
        var (page, _) = await LoginAsOperatorOnScheduleAsync();

        page.Url.Should().Be(_fixture.WebBaseUrl + "/schedule");
        await page.GetByText("Agenda").First.WaitForAsync();
    }

    /// <summary>PROPERTY_OWNER holds only SCHEDULE:READ:OWN_OWNER (IdentityCatalogSeed) — never SCHEDULE:MANAGE/SCHEDULE:READ, the two codes the /schedule route guard exact-matches — so it is the real seeded role that must be denied.</summary>
    [Fact]
    public async Task User_without_SCHEDULE_permission_is_redirected_to_forbidden()
    {
        var adminPage = await NewPageAsync();
        await adminPage.GotoAsync(_fixture.WebBaseUrl + "/login");
        await adminPage.GetByLabel("Empresa").FillAsync(WebE2EFixture.TenantSlugValue);
        await adminPage.GetByLabel("E-mail").FillAsync(WebE2EFixture.AdminEmail);
        await adminPage.GetByLabel("Senha").FillAsync(WebE2EFixture.AdminPassword);
        var adminProfileRequest = await adminPage.RunAndWaitForRequestAsync(
            async () => await adminPage.GetByRole(AriaRole.Button, new() { Name = "Entrar" }).ClickAsync(),
            req => req.Url.Contains("/api/v1/users/me") && req.Method == "GET");
        var adminToken = await adminProfileRequest.HeaderValueAsync("Authorization") ?? throw new InvalidOperationException("No Authorization header captured.");
        await adminPage.WaitForURLAsync(_fixture.WebBaseUrl + "/");

        var ownerEmail = $"schedule-owner-{Guid.NewGuid():N}@e2e-playwright.test";
        const string ownerPassword = "Correct-Horse-Battery-Staple-44!";
        var createUserResponse = await adminPage.Context.APIRequest.PostAsync(
            _fixture.ApiBaseUrl + "/api/v1/users",
            new APIRequestContextOptions
            {
                Headers = new Dictionary<string, string> { ["Authorization"] = adminToken },
                DataObject = new { fullName = "E2E Schedule Property Owner", email = ownerEmail, initialPassword = ownerPassword, roleCode = "PROPERTY_OWNER" },
            });
        createUserResponse.Ok.Should().BeTrue($"test-data setup via the real API must succeed (status {createUserResponse.Status})");

        var page = await NewPageAsync();
        await page.GotoAsync(_fixture.WebBaseUrl + "/login");
        await page.GetByLabel("Empresa").FillAsync(WebE2EFixture.TenantSlugValue);
        await page.GetByLabel("E-mail").FillAsync(ownerEmail);
        await page.GetByLabel("Senha").FillAsync(ownerPassword);
        await page.GetByRole(AriaRole.Button, new() { Name = "Entrar" }).ClickAsync();
        await page.WaitForURLAsync(_fixture.WebBaseUrl + "/");

        (await page.GetByRole(AriaRole.Link, new() { Name = "Agenda" }).CountAsync()).Should().Be(0, "PROPERTY_OWNER is never granted SCHEDULE:MANAGE/SCHEDULE:READ, so the nav item must not render at all");

        await page.GotoAsync(_fixture.WebBaseUrl + "/schedule");

        await page.WaitForURLAsync(_fixture.WebBaseUrl + "/forbidden");
        page.Url.Should().Be(_fixture.WebBaseUrl + "/forbidden", "the route guard must deny direct navigation by real permission, not just hide the nav link");
    }

    // ---- 4/5/6/7: real Reservation and Cleaning appear, correct times ----

    [Fact]
    public async Task A_real_Reservation_appears_using_CheckInAt_and_CheckOutAt()
    {
        var (page, token) = await LoginAsAdminOnScheduleAsync(timezoneId: "UTC");
        var propertyId = await CreateActivePropertyViaApiAsync(page, token, "E2E-SCH-RES-1", "E2E Schedule Reservation Property");
        var reservationId = await CreateReservationViaApiAsync(page, token, propertyId, "E2E Schedule Guest", "2026-08-20T14:00:00Z", "2026-08-22T11:00:00Z");

        await page.Locator(".fc-next-button").ClickAsync();

        var response = await page.Context.APIRequest.GetAsync(
            _fixture.ApiBaseUrl + "/api/v1/schedule?from=2026-08-17T00:00:00Z&to=2026-08-24T00:00:00Z",
            new APIRequestContextOptions { Headers = new Dictionary<string, string> { ["Authorization"] = token } });
        var items = await response.JsonAsync();
        var reservationItem = items!.Value.EnumerateArray().Single(i => i.GetProperty("sourceReferenceId").GetString() == reservationId);
        reservationItem.GetProperty("type").GetString().Should().Be("Reservation");
        reservationItem.GetProperty("startAtUtc").GetDateTimeOffset().Should().Be(DateTimeOffset.Parse("2026-08-20T14:00:00Z"));
        reservationItem.GetProperty("endAtUtc")!.GetDateTimeOffset().Should().Be(DateTimeOffset.Parse("2026-08-22T11:00:00Z"));
    }

    [Fact]
    public async Task A_real_Cleaning_appears_at_its_ScheduledAtUtc()
    {
        var (page, token) = await LoginAsAdminOnScheduleAsync();
        var propertyId = await CreateActivePropertyViaApiAsync(page, token, "E2E-SCH-CLN-1", "E2E Schedule Cleaning Property");
        await WaitUntilKnownToHousekeepingAsync(page, token, propertyId);
        var scheduledAtUtc = "2026-08-21T18:00:00Z";
        var cleaningId = await CreateScheduledCleaningViaApiAsync(page, token, propertyId, scheduledAtUtc);

        await WaitUntilVisibleInScheduleAsync(page, token, "2026-08-17T00:00:00Z", "2026-08-24T00:00:00Z", cleaningId);

        var response = await page.Context.APIRequest.GetAsync(
            _fixture.ApiBaseUrl + "/api/v1/schedule?from=2026-08-17T00:00:00Z&to=2026-08-24T00:00:00Z",
            new APIRequestContextOptions { Headers = new Dictionary<string, string> { ["Authorization"] = token } });
        var items = await response.JsonAsync();
        var cleaningItem = items!.Value.EnumerateArray().Single(i => i.GetProperty("sourceReferenceId").GetString() == cleaningId);
        cleaningItem.GetProperty("type").GetString().Should().Be("Cleaning");
        cleaningItem.GetProperty("startAtUtc").GetDateTimeOffset().Should().Be(DateTimeOffset.Parse(scheduledAtUtc), "the Cleaning must render at exactly its own ScheduledAtUtc, no shift");
    }

    [Fact]
    public async Task Reservation_and_Cleaning_are_distinguishable_beyond_color()
    {
        var (page, token) = await LoginAsAdminOnScheduleAsync();
        var propertyId = await CreateActivePropertyViaApiAsync(page, token, "E2E-SCH-DIST-1", "E2E Schedule Distinguish Property");
        await WaitUntilKnownToHousekeepingAsync(page, token, propertyId);

        var reservationId = await CreateReservationViaApiAsync(page, token, propertyId, "E2E Distinguish Guest", "2026-08-20T14:00:00Z", "2026-08-22T11:00:00Z");
        var cleaningId = await CreateScheduledCleaningViaApiAsync(page, token, propertyId, "2026-08-21T18:00:00Z");
        await WaitUntilVisibleInScheduleAsync(page, token, "2026-08-17T00:00:00Z", "2026-08-24T00:00:00Z", cleaningId);
        _ = reservationId;

        await page.ReloadAsync();
        await page.Locator(".fc-next-button").ClickAsync();

        var reservationEvent = page.Locator(".schedule-event-reservation").First;
        var cleaningEvent = page.Locator(".schedule-event-cleaning").First;
        await reservationEvent.WaitForAsync();
        await cleaningEvent.WaitForAsync();

        (await reservationEvent.InnerTextAsync()).Should().Contain("Reserva", "type must be stated as text, never color alone");
        (await cleaningEvent.InnerTextAsync()).Should().Contain("Faxina", "type must be stated as text, never color alone");
        (await reservationEvent.GetAttributeAsync("class")).Should().NotBe(await cleaningEvent.GetAttributeAsync("class"));
    }

    // ---- 9/10/11/12: Day/Week/Month views, prev/today/next navigation ----

    [Fact]
    public async Task Day_Week_and_Month_views_are_all_selectable()
    {
        var (page, _) = await LoginAsAdminOnScheduleAsync();

        await page.Locator(".fc-timeGridDay-button").ClickAsync();
        (await page.Locator(".fc-timeGridDay-view").CountAsync()).Should().BeGreaterThan(0);

        await page.Locator(".fc-dayGridMonth-button").ClickAsync();
        (await page.Locator(".fc-dayGridMonth-view").CountAsync()).Should().BeGreaterThan(0);

        await page.Locator(".fc-timeGridWeek-button").ClickAsync();
        (await page.Locator(".fc-timeGridWeek-view").CountAsync()).Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Previous_Today_and_Next_navigation_change_the_visible_range()
    {
        var loginPage = await NewPageAsync();
        await loginPage.GotoAsync(_fixture.WebBaseUrl + "/login");
        await loginPage.GetByLabel("Empresa").FillAsync(WebE2EFixture.TenantSlugValue);
        await loginPage.GetByLabel("E-mail").FillAsync(WebE2EFixture.AdminEmail);
        await loginPage.GetByLabel("Senha").FillAsync(WebE2EFixture.AdminPassword);
        await loginPage.GetByRole(AriaRole.Button, new() { Name = "Entrar" }).ClickAsync();
        await loginPage.WaitForURLAsync(_fixture.WebBaseUrl + "/");

        var initialResponse = await loginPage.RunAndWaitForResponseAsync(
            async () => await loginPage.GetByRole(AriaRole.Link, new() { Name = "Agenda" }).ClickAsync(),
            r => r.Url.Contains("/api/v1/schedule") && r.Request.Method == "GET");
        var initialQuery = System.Web.HttpUtility.ParseQueryString(new Uri(initialResponse.Url).Query);
        var initialFrom = initialQuery["from"];

        var nextResponse = await loginPage.RunAndWaitForResponseAsync(
            async () => await loginPage.Locator(".fc-next-button").ClickAsync(),
            r => r.Url.Contains("/api/v1/schedule") && r.Request.Method == "GET");
        var nextQuery = System.Web.HttpUtility.ParseQueryString(new Uri(nextResponse.Url).Query);
        nextQuery["from"].Should().NotBe(initialFrom, "Next must request a later range than the initial load");

        var previousResponse = await loginPage.RunAndWaitForResponseAsync(
            async () => await loginPage.Locator(".fc-prev-button").ClickAsync(),
            r => r.Url.Contains("/api/v1/schedule") && r.Request.Method == "GET");
        var previousQuery = System.Web.HttpUtility.ParseQueryString(new Uri(previousResponse.Url).Query);
        previousQuery["from"].Should().Be(initialFrom, "Next then Previous must return to the original range");

        // Next again first, so the view is genuinely away from today's own
        // week — clicking Today from a range that already contains today
        // would be a real no-op (FullCalendar never re-fetches a range that
        // hasn't changed), which is correct app behavior but would make this
        // specific assertion meaningless.
        await loginPage.RunAndWaitForResponseAsync(
            async () => await loginPage.Locator(".fc-next-button").ClickAsync(),
            r => r.Url.Contains("/api/v1/schedule") && r.Request.Method == "GET");

        var todayResponse = await loginPage.RunAndWaitForResponseAsync(
            async () => await loginPage.Locator(".fc-today-button").ClickAsync(),
            r => r.Url.Contains("/api/v1/schedule") && r.Request.Method == "GET");
        var todayQuery = System.Web.HttpUtility.ParseQueryString(new Uri(todayResponse.Url).Query);
        todayQuery["from"].Should().Be(initialFrom, "Today must return to the range containing the current date");
    }

    // ---- 13/14: EventType filters -----------------------------------

    [Fact]
    public async Task Filtering_by_EventType_Reservation_hides_Cleaning_events()
    {
        var (page, token) = await LoginAsAdminOnScheduleAsync();
        var propertyId = await CreateActivePropertyViaApiAsync(page, token, "E2E-SCH-FILT-1", "E2E Schedule Filter Property");
        await WaitUntilKnownToHousekeepingAsync(page, token, propertyId);
        await CreateReservationViaApiAsync(page, token, propertyId, "E2E Filter Guest", "2026-08-20T14:00:00Z", "2026-08-22T11:00:00Z");
        var cleaningId = await CreateScheduledCleaningViaApiAsync(page, token, propertyId, "2026-08-21T18:00:00Z");
        await WaitUntilVisibleInScheduleAsync(page, token, "2026-08-17T00:00:00Z", "2026-08-24T00:00:00Z", cleaningId);

        await page.ReloadAsync();
        await page.Locator(".fc-next-button").ClickAsync();
        await page.GetByLabel("Tipo").ClickAsync();
        await page.RunAndWaitForResponseAsync(
            async () => await page.GetByRole(AriaRole.Option, new() { Name = "Reservas" }).ClickAsync(),
            r => r.Url.Contains("/api/v1/schedule") && r.Url.Contains("eventType=Reservation"));

        (await page.Locator(".schedule-event-reservation").CountAsync()).Should().BeGreaterThan(0);
        (await page.Locator(".schedule-event-cleaning").CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Filtering_by_EventType_Cleaning_hides_Reservation_events()
    {
        var (page, token) = await LoginAsAdminOnScheduleAsync();
        var propertyId = await CreateActivePropertyViaApiAsync(page, token, "E2E-SCH-FILT-2", "E2E Schedule Filter Property 2");
        await WaitUntilKnownToHousekeepingAsync(page, token, propertyId);
        await CreateReservationViaApiAsync(page, token, propertyId, "E2E Filter Guest 2", "2026-08-20T14:00:00Z", "2026-08-22T11:00:00Z");
        var cleaningId = await CreateScheduledCleaningViaApiAsync(page, token, propertyId, "2026-08-21T18:00:00Z");
        await WaitUntilVisibleInScheduleAsync(page, token, "2026-08-17T00:00:00Z", "2026-08-24T00:00:00Z", cleaningId);

        await page.ReloadAsync();
        await page.Locator(".fc-next-button").ClickAsync();
        await page.GetByLabel("Tipo").ClickAsync();
        await page.RunAndWaitForResponseAsync(
            async () => await page.GetByRole(AriaRole.Option, new() { Name = "Faxinas" }).ClickAsync(),
            r => r.Url.Contains("/api/v1/schedule") && r.Url.Contains("eventType=Cleaning"));

        (await page.Locator(".schedule-event-cleaning").CountAsync()).Should().BeGreaterThan(0);
        (await page.Locator(".schedule-event-reservation").CountAsync()).Should().Be(0);
    }

    // ---- 15: visible range is sent to the backend ------------------------

    [Fact]
    public async Task The_calendars_visible_range_is_sent_to_the_backend()
    {
        var page = await NewPageAsync();
        await page.GotoAsync(_fixture.WebBaseUrl + "/login");
        await page.GetByLabel("Empresa").FillAsync(WebE2EFixture.TenantSlugValue);
        await page.GetByLabel("E-mail").FillAsync(WebE2EFixture.AdminEmail);
        await page.GetByLabel("Senha").FillAsync(WebE2EFixture.AdminPassword);
        await page.GetByRole(AriaRole.Button, new() { Name = "Entrar" }).ClickAsync();
        await page.WaitForURLAsync(_fixture.WebBaseUrl + "/");

        var scheduleRequest = await page.RunAndWaitForRequestAsync(
            async () => await page.GetByRole(AriaRole.Link, new() { Name = "Agenda" }).ClickAsync(),
            req => req.Url.Contains("/api/v1/schedule") && req.Method == "GET");

        var uri = new Uri(scheduleRequest.Url);
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        query["from"].Should().NotBeNullOrEmpty("the visible range's start must be sent, never a full-history fetch");
        query["to"].Should().NotBeNullOrEmpty("the visible range's end must be sent, never a full-history fetch");
        var from = DateTimeOffset.Parse(query["from"]!);
        var to = DateTimeOffset.Parse(query["to"]!);
        (to - from).Should().BeLessThan(TimeSpan.FromDays(40), "the default Week/Day view's range must be small, never a full-history window");
    }

    // ---- 16/17: cross-tenant isolation and empty state -------------------

    [Fact]
    public async Task Another_tenants_events_never_appear_and_the_empty_state_renders()
    {
        var (adminPage, adminToken) = await LoginAsAdminOnScheduleAsync();
        var propertyId = await CreateActivePropertyViaApiAsync(adminPage, adminToken, "E2E-SCH-XT-1", "E2E Schedule Cross-Tenant Property");
        await CreateReservationViaApiAsync(adminPage, adminToken, propertyId, "E2E Cross-Tenant Guest", "2026-08-20T14:00:00Z", "2026-08-22T11:00:00Z");

        var (secondTenantId, secondSlug, secondAdminEmail, secondAdminPassword) = await _fixture.CreateAdditionalTenantWithAdminAsync();
        _ = secondTenantId;

        var secondPage = await NewPageAsync();
        await secondPage.GotoAsync(_fixture.WebBaseUrl + "/login");
        await secondPage.GetByLabel("Empresa").FillAsync(secondSlug);
        await secondPage.GetByLabel("E-mail").FillAsync(secondAdminEmail);
        await secondPage.GetByLabel("Senha").FillAsync(secondAdminPassword);
        await secondPage.GetByRole(AriaRole.Button, new() { Name = "Entrar" }).ClickAsync();
        await secondPage.WaitForURLAsync(_fixture.WebBaseUrl + "/");
        await secondPage.GetByRole(AriaRole.Link, new() { Name = "Agenda" }).ClickAsync();
        await secondPage.WaitForURLAsync(_fixture.WebBaseUrl + "/schedule");

        await secondPage.GetByRole(AriaRole.Button, new() { Name = "Next" }).ClickAsync();
        await secondPage.GetByRole(AriaRole.Button, new() { Name = "Next" }).ClickAsync();

        (await secondPage.Locator(".fc-event").CountAsync()).Should().Be(0, "a brand-new tenant must never see another tenant's Reservation/Cleaning");
        await secondPage.GetByText("Nenhum evento neste período.").WaitForAsync();
    }

    // ---- 18: responsive 375px --------------------------------------------

    [Fact]
    public async Task The_Agenda_is_usable_at_375px()
    {
        var context = await _fixture.Browser.NewContextAsync(new BrowserNewContextOptions { ViewportSize = new ViewportSize { Width = 375, Height = 812 } });
        var page = await context.NewPageAsync();
        await page.GotoAsync(_fixture.WebBaseUrl + "/login");
        await page.GetByLabel("Empresa").FillAsync(WebE2EFixture.TenantSlugValue);
        await page.GetByLabel("E-mail").FillAsync(WebE2EFixture.AdminEmail);
        await page.GetByLabel("Senha").FillAsync(WebE2EFixture.AdminPassword);
        await page.GetByRole(AriaRole.Button, new() { Name = "Entrar" }).ClickAsync();
        await page.WaitForURLAsync(_fixture.WebBaseUrl + "/");
        await page.GotoAsync(_fixture.WebBaseUrl + "/schedule");

        await page.GetByText("Agenda").First.WaitForAsync();

        var overflows = await page.EvaluateAsync<bool>("document.documentElement.scrollWidth > window.innerWidth + 1");
        overflows.Should().BeFalse("the Agenda must never introduce horizontal overflow at 375px");

        (await page.Locator(".fc-timeGridDay-view, .fc-timeGridWeek-view").CountAsync()).Should().BeGreaterThan(0, "the calendar itself must still render at 375px");
    }

    // ---- Additional: real status-lifecycle update reflected --------------

    [Fact]
    public async Task A_real_Cleaning_status_update_Assigned_is_reflected_in_the_schedule()
    {
        var (page, token) = await LoginAsAdminOnScheduleAsync();
        var propertyId = await CreateActivePropertyViaApiAsync(page, token, "E2E-SCH-LIFE-1", "E2E Schedule Lifecycle Property");
        await WaitUntilKnownToHousekeepingAsync(page, token, propertyId);
        var cleaningId = await CreateScheduledCleaningViaApiAsync(page, token, propertyId, "2026-08-21T18:00:00Z");
        await WaitUntilVisibleInScheduleAsync(page, token, "2026-08-17T00:00:00Z", "2026-08-24T00:00:00Z", cleaningId);

        var housekeeperEmail = $"schedule-hk-{Guid.NewGuid():N}@e2e-playwright.test";
        var createHousekeeperResponse = await page.Context.APIRequest.PostAsync(
            _fixture.ApiBaseUrl + "/api/v1/users",
            new APIRequestContextOptions
            {
                Headers = new Dictionary<string, string> { ["Authorization"] = token },
                DataObject = new { fullName = "E2E Schedule Housekeeper", email = housekeeperEmail, initialPassword = "Correct-Horse-Battery-Staple-33!", roleCode = "HOUSEKEEPER" },
            });
        createHousekeeperResponse.Ok.Should().BeTrue($"test-data setup via the real API must succeed (status {createHousekeeperResponse.Status})");
        var housekeeperBody = await createHousekeeperResponse.JsonAsync();
        var housekeeperUserId = housekeeperBody!.Value.GetProperty("id").GetString();

        var assignResponse = await page.Context.APIRequest.PostAsync(
            _fixture.ApiBaseUrl + $"/api/v1/cleanings/{cleaningId}/assign",
            new APIRequestContextOptions
            {
                Headers = new Dictionary<string, string> { ["Authorization"] = token },
                DataObject = new { housekeeperUserId },
            });
        assignResponse.Ok.Should().BeTrue($"real status transition must succeed (status {assignResponse.Status})");

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        JsonElement? updatedItem = null;
        while (DateTime.UtcNow < deadline)
        {
            var response = await page.Context.APIRequest.GetAsync(
                _fixture.ApiBaseUrl + "/api/v1/schedule?from=2026-08-17T00:00:00Z&to=2026-08-24T00:00:00Z",
                new APIRequestContextOptions { Headers = new Dictionary<string, string> { ["Authorization"] = token } });
            var items = await response.JsonAsync();
            var candidate = items!.Value.EnumerateArray().FirstOrDefault(i => i.GetProperty("sourceReferenceId").GetString() == cleaningId);
            if (candidate.ValueKind == JsonValueKind.Object && candidate.GetProperty("status").GetString() == "Assigned")
            {
                updatedItem = candidate;
                break;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(300));
        }

        updatedItem.Should().NotBeNull("the real Assigned status transition must propagate into GET /api/v1/schedule within 20s");
        updatedItem!.Value.GetProperty("housekeeperUserId").GetString().Should().Be(housekeeperUserId);
    }

    // ---- Additional: timezone correctness, explicit browser TimezoneId ----

    [Fact]
    public async Task Reservation_and_Cleaning_times_render_with_no_timezone_shift()
    {
        var (page, token) = await LoginAsAdminOnScheduleAsync(timezoneId: "UTC");
        var propertyId = await CreateActivePropertyViaApiAsync(page, token, "E2E-SCH-TZ-1", "E2E Schedule Timezone Property");
        await WaitUntilKnownToHousekeepingAsync(page, token, propertyId);

        var reservationId = await CreateReservationViaApiAsync(page, token, propertyId, "E2E Timezone Guest", "2026-08-20T15:00:00Z", "2026-08-22T11:00:00Z");
        var cleaningId = await CreateScheduledCleaningViaApiAsync(page, token, propertyId, "2026-08-21T15:00:00Z");
        await WaitUntilVisibleInScheduleAsync(page, token, "2026-08-17T00:00:00Z", "2026-08-24T00:00:00Z", cleaningId);
        _ = reservationId;

        await page.ReloadAsync();
        await page.Locator(".fc-next-button").ClickAsync();

        // Context timezone is explicitly UTC, so 15:00Z must render as "3:00"
        // local (FullCalendar's default 12-hour label) — never shifted by an
        // unrelated host machine's own timezone (mandate §18).
        var reservationEvent = page.Locator(".schedule-event-reservation").First;
        var cleaningEvent = page.Locator(".schedule-event-cleaning").First;
        await reservationEvent.WaitForAsync();
        await cleaningEvent.WaitForAsync();
        (await reservationEvent.InnerTextAsync()).Should().Contain("3:00");
        (await cleaningEvent.InnerTextAsync()).Should().Contain("3:00");
    }
}
