using System.Text.Json;
using FluentAssertions;
using Microsoft.Playwright;

namespace IHostPro.Web.Tests.E2E;

/// <summary>
/// Real-browser coverage for the rest of Fase 4 Incremento 4 (Gestão de
/// Reservas) — listagem, criação, edição, cancelamento, e os erros reais que
/// o backend distingue (imóvel inativo, capacidade excedida, conflito de
/// período, conflito de concorrência, cancelamento repetido) — once the
/// authorization gate (<see cref="ReservationsAuthorizationE2ETests"/>) was
/// confirmed green. Drives the real, unmodified <c>IHostPro.Web</c> against
/// the real, unmodified <c>IHostPro.Api</c> — see <see cref="WebE2EFixture"/>.
/// Every test creates its own throwaway property (and, where the flow under
/// test is not itself creation, its own reservation) directly through the
/// real API — using the ADMIN's own real bearer token, captured off a real
/// network request — then drives only the one UI action under test through
/// the real Angular app. Mirrors <see cref="PropertyManagementE2ETests"/>'s
/// pattern exactly.
/// </summary>
[Collection(WebE2EFixtureCollection.Name)]
public sealed class ReservationsE2ETests
{
    private readonly WebE2EFixture _fixture;

    public ReservationsE2ETests(WebE2EFixture fixture) => _fixture = fixture;

    private async Task<IPage> NewPageAsync()
    {
        var context = await _fixture.Browser.NewContextAsync();
        return await context.NewPageAsync();
    }

    /// <summary>Logs in as ADMIN and returns the page positioned on /reservations, plus the real bearer token the app itself used for GET /api/v1/users/me — for API-level test-data setup only, never for driving the UI.</summary>
    private async Task<(IPage Page, string BearerToken)> LoginAsAdminOnReservationsAsync()
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
        await page.GetByRole(AriaRole.Link, new() { Name = "Reservas" }).ClickAsync();
        await page.WaitForURLAsync(_fixture.WebBaseUrl + "/reservations");

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

    /// <summary>Creates a throwaway, ACTIVE property directly through the real API — Create/UpdateReservationCommandHandler both reject an inactive property with Reservations.PropertyNotActive (ADR-014's IPropertyReservationEligibilityReader.IsActive check), so every reservation flow needs one already active. Returns its id.</summary>
    private async Task<string> CreateActivePropertyViaApiAsync(IPage page, string bearerToken, string code, string name, int capacity = 4)
    {
        var createResponse = await page.Context.APIRequest.PostAsync(
            _fixture.ApiBaseUrl + "/api/v1/properties",
            new APIRequestContextOptions
            {
                Headers = new Dictionary<string, string> { ["Authorization"] = bearerToken },
                DataObject = new { code, name, capacity, condominiumId = (string?)null, address = SampleAddress },
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

    /// <summary>Creates a throwaway reservation directly through the real API. Returns its id.</summary>
    private async Task<string> CreateReservationViaApiAsync(
        IPage page, string bearerToken, string propertyId, string guestName, string? guestPhone,
        string checkInAtIso, string checkOutAtIso, int guestCount)
    {
        var response = await page.Context.APIRequest.PostAsync(
            _fixture.ApiBaseUrl + "/api/v1/reservations",
            new APIRequestContextOptions
            {
                Headers = new Dictionary<string, string> { ["Authorization"] = bearerToken },
                DataObject = new { propertyId, guestName, guestPhone, checkInAt = checkInAtIso, checkOutAt = checkOutAtIso, guestCount },
            });
        response.Ok.Should().BeTrue($"test-data setup via the real API must succeed (status {response.Status})");
        var body = await response.JsonAsync();
        return body!.Value.GetProperty("id").GetString()!;
    }

    private async Task<JsonElement> GetReservationViaApiAsync(IPage page, string bearerToken, string reservationId)
    {
        var response = await page.Context.APIRequest.GetAsync(
            _fixture.ApiBaseUrl + $"/api/v1/reservations/{reservationId}",
            new APIRequestContextOptions { Headers = new Dictionary<string, string> { ["Authorization"] = bearerToken } });
        response.Ok.Should().BeTrue($"verification via the real API must succeed (status {response.Status})");
        return (await response.JsonAsync())!.Value;
    }

    private static async Task<string> AlertTextAsync(ILocator dialog)
    {
        var alert = dialog.GetByRole(AriaRole.Alert);
        await alert.WaitForAsync();
        return (await alert.TextContentAsync())!;
    }

    [Fact]
    public async Task Admin_views_the_reservations_listing()
    {
        var (page, token) = await LoginAsAdminOnReservationsAsync();
        var propertyId = await CreateActivePropertyViaApiAsync(page, token, "E2E-RES-LIST-1", "E2E Reservation Listing Property");
        const string guestName = "E2E Listing Guest";
        await CreateReservationViaApiAsync(page, token, propertyId, guestName, null, "2026-09-01T14:00:00Z", "2026-09-05T11:00:00Z", 2);

        await page.ReloadAsync();

        var row = page.Locator("tr", new PageLocatorOptions { HasText = guestName });
        await row.WaitForAsync();
        (await row.GetByText("Confirmada").CountAsync()).Should().BeGreaterThan(0, "a newly-created reservation starts Confirmed");
    }

    [Fact]
    public async Task Admin_creates_a_valid_reservation()
    {
        var (page, token) = await LoginAsAdminOnReservationsAsync();
        var propertyId = await CreateActivePropertyViaApiAsync(page, token, "E2E-RES-CREATE-1", "E2E Reservation Create Property");
        const string guestName = "E2E Create Guest";

        await page.GetByRole(AriaRole.Button, new() { Name = "Nova reserva" }).ClickAsync();
        var dialog = page.GetByRole(AriaRole.Dialog);
        await dialog.GetByLabel("ID do imóvel").FillAsync(propertyId);
        await dialog.GetByLabel("Nome do hóspede").FillAsync(guestName);
        await dialog.GetByLabel("Check-in").FillAsync("2026-09-10T14:00");
        await dialog.GetByLabel("Check-out").FillAsync("2026-09-12T11:00");
        await dialog.GetByLabel("Quantidade de hóspedes").FillAsync("2");
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Salvar" }).ClickAsync();

        await page.GetByText("Reserva criada com sucesso.").WaitForAsync();
        await page.Locator("table").GetByText(guestName).WaitForAsync();
    }

    [Fact]
    public async Task A_period_conflict_is_presented_correctly()
    {
        var (page, token) = await LoginAsAdminOnReservationsAsync();
        var propertyId = await CreateActivePropertyViaApiAsync(page, token, "E2E-RES-CONFLICT-1", "E2E Reservation Conflict Property");
        await CreateReservationViaApiAsync(page, token, propertyId, "E2E Existing Guest", null, "2026-09-15T14:00:00Z", "2026-09-20T11:00:00Z", 2);

        await page.GetByRole(AriaRole.Button, new() { Name = "Nova reserva" }).ClickAsync();
        var dialog = page.GetByRole(AriaRole.Dialog);
        await dialog.GetByLabel("ID do imóvel").FillAsync(propertyId);
        await dialog.GetByLabel("Nome do hóspede").FillAsync("E2E Overlapping Guest");
        // Overlaps the existing reservation's own 15th-20th window.
        await dialog.GetByLabel("Check-in").FillAsync("2026-09-17T14:00");
        await dialog.GetByLabel("Check-out").FillAsync("2026-09-19T11:00");
        await dialog.GetByLabel("Quantidade de hóspedes").FillAsync("2");
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Salvar" }).ClickAsync();

        (await AlertTextAsync(dialog)).Should().Contain("Não foi possível concluir a ação devido a um conflito");
        (await dialog.CountAsync()).Should().Be(1, "the dialog must remain open on a rejected submission, never silently close");
    }

    [Fact]
    public async Task A_capacity_limit_is_presented_correctly()
    {
        var (page, token) = await LoginAsAdminOnReservationsAsync();
        var propertyId = await CreateActivePropertyViaApiAsync(page, token, "E2E-RES-CAPACITY-1", "E2E Reservation Capacity Property", capacity: 2);

        await page.GetByRole(AriaRole.Button, new() { Name = "Nova reserva" }).ClickAsync();
        var dialog = page.GetByRole(AriaRole.Dialog);
        await dialog.GetByLabel("ID do imóvel").FillAsync(propertyId);
        await dialog.GetByLabel("Nome do hóspede").FillAsync("E2E Over Capacity Guest");
        await dialog.GetByLabel("Check-in").FillAsync("2026-09-22T14:00");
        await dialog.GetByLabel("Check-out").FillAsync("2026-09-24T11:00");
        await dialog.GetByLabel("Quantidade de hóspedes").FillAsync("5");
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Salvar" }).ClickAsync();

        (await AlertTextAsync(dialog)).Should().Contain("A quantidade de hóspedes excede a capacidade do imóvel.");
    }

    [Fact]
    public async Task Admin_edits_a_reservation()
    {
        var (page, token) = await LoginAsAdminOnReservationsAsync();
        var propertyId = await CreateActivePropertyViaApiAsync(page, token, "E2E-RES-EDIT-1", "E2E Reservation Edit Property");
        const string originalName = "E2E Editable Reservation Guest";
        const string updatedName = "E2E Edited Reservation Guest";
        await CreateReservationViaApiAsync(page, token, propertyId, originalName, null, "2026-09-25T14:00:00Z", "2026-09-27T11:00:00Z", 2);
        await page.ReloadAsync();
        var row = page.Locator("tr", new PageLocatorOptions { HasText = originalName });
        await row.WaitForAsync();
        await row.GetByRole(AriaRole.Button, new() { Name = $"Ações para {originalName}" }).ClickAsync();
        await page.GetByRole(AriaRole.Menuitem, new() { Name = "Editar" }).ClickAsync();

        var dialog = page.GetByRole(AriaRole.Dialog);
        var nameField = dialog.GetByLabel("Nome do hóspede");
        await nameField.FillAsync(string.Empty);
        await nameField.FillAsync(updatedName);
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Salvar" }).ClickAsync();

        await page.GetByText("Reserva atualizada com sucesso.").WaitForAsync();
        await page.Locator("table").GetByText(updatedName).WaitForAsync();
    }

    [Fact]
    public async Task Admin_clears_guestPhone_by_sending_null()
    {
        var (page, token) = await LoginAsAdminOnReservationsAsync();
        var propertyId = await CreateActivePropertyViaApiAsync(page, token, "E2E-RES-PHONE-1", "E2E Reservation Phone Property");
        const string guestName = "E2E Phone Clearing Guest";
        var reservationId = await CreateReservationViaApiAsync(page, token, propertyId, guestName, "+55 11 90000-0000", "2026-09-28T14:00:00Z", "2026-09-30T11:00:00Z", 2);
        await page.ReloadAsync();
        var row = page.Locator("tr", new PageLocatorOptions { HasText = guestName });
        await row.WaitForAsync();
        await row.GetByRole(AriaRole.Button, new() { Name = $"Ações para {guestName}" }).ClickAsync();
        await page.GetByRole(AriaRole.Menuitem, new() { Name = "Editar" }).ClickAsync();

        var dialog = page.GetByRole(AriaRole.Dialog);
        var phoneField = dialog.GetByLabel("Telefone do hóspede");
        await phoneField.WaitForAsync();
        (await phoneField.InputValueAsync()).Should().Be("+55 11 90000-0000", "the edit dialog must pre-fill the reservation's current guestPhone");
        await phoneField.FillAsync(string.Empty);
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Salvar" }).ClickAsync();

        await page.GetByText("Reserva atualizada com sucesso.").WaitForAsync();
        var updated = await GetReservationViaApiAsync(page, token, reservationId);
        updated.GetProperty("guestPhone").ValueKind.Should().Be(JsonValueKind.Null, "clearing the field in the UI must send an explicit JSON null, not merely omit the key");
    }

    [Fact]
    public async Task Admin_cancels_a_reservation()
    {
        var (page, token) = await LoginAsAdminOnReservationsAsync();
        var propertyId = await CreateActivePropertyViaApiAsync(page, token, "E2E-RES-CANCEL-1", "E2E Reservation Cancel Property");
        const string guestName = "E2E Cancellable Guest";
        await CreateReservationViaApiAsync(page, token, propertyId, guestName, null, "2026-10-05T14:00:00Z", "2026-10-07T11:00:00Z", 2);
        await page.ReloadAsync();
        var row = page.Locator("tr", new PageLocatorOptions { HasText = guestName });
        await row.WaitForAsync();
        await row.GetByRole(AriaRole.Button, new() { Name = $"Ações para {guestName}" }).ClickAsync();
        await page.GetByRole(AriaRole.Menuitem, new() { Name = "Cancelar reserva" }).ClickAsync();

        await page.GetByRole(AriaRole.Heading, new() { Name = "Cancelar reserva" }).WaitForAsync();
        await page.GetByRole(AriaRole.Dialog).GetByRole(AriaRole.Button, new() { Name = "Cancelar reserva" }).ClickAsync();

        await page.GetByText("Reserva cancelada com sucesso.").WaitForAsync();
        await row.GetByText("Cancelada").WaitForAsync();
        await row.GetByRole(AriaRole.Button, new() { Name = $"Ações para {guestName}" }).ClickAsync();
        var menu = page.GetByRole(AriaRole.Menu);
        (await menu.GetByRole(AriaRole.Menuitem, new() { Name = "Editar" }).CountAsync()).Should().Be(0, "a cancelled reservation is terminal — no further edit is offered");
        (await menu.GetByRole(AriaRole.Menuitem, new() { Name = "Cancelar reserva" }).CountAsync()).Should().Be(0, "a cancelled reservation is terminal — cancel cannot be offered again");
    }

    /// <summary>
    /// CancelReservationCommandHandler rejects cancelling an already-Cancelled reservation with
    /// ReservationAlreadyCancelled (409) — reproduced here deterministically (no race required,
    /// unlike the concurrency-conflict flow above) by cancelling the reservation directly through
    /// the real API behind a still-open, stale row menu, then clicking that same stale "Cancelar
    /// reserva" item: the real backend rejects it and the UI must show the classified conflict
    /// error, never crash or silently succeed.
    /// </summary>
    [Fact]
    public async Task A_repeated_cancellation_is_handled_correctly()
    {
        var (page, token) = await LoginAsAdminOnReservationsAsync();
        var propertyId = await CreateActivePropertyViaApiAsync(page, token, "E2E-RES-RECANCEL-1", "E2E Reservation Repeated Cancel Property");
        const string guestName = "E2E Repeated Cancel Guest";
        var reservationId = await CreateReservationViaApiAsync(page, token, propertyId, guestName, null, "2026-10-10T14:00:00Z", "2026-10-12T11:00:00Z", 2);
        await page.ReloadAsync();

        // The reservations list is paginated (page size 10) and ordered by
        // CheckInAt ascending — across this fixture's own full E2E suite
        // (every test shares one Postgres/tenant, per WebE2EFixtureCollection),
        // dozens of other reservations accumulate, which can push this one
        // past the default first page long before it's ever visible. Filters
        // by this test's own dedicated, real propertyId (the real UI filter
        // already used elsewhere in this file/ReservationFormDialog, never a
        // new one) instead of assuming page 1 — deterministic regardless of
        // how much other data exists, since each test creates its own
        // throwaway property.
        await page.GetByLabel("ID do imóvel").FillAsync(propertyId);
        await page.RunAndWaitForResponseAsync(
            async () => await page.GetByRole(AriaRole.Button, new() { Name = "Filtrar" }).ClickAsync(),
            r => r.Url.Contains("/api/v1/reservations") && r.Request.Method == "GET");

        var row = page.Locator("tr", new PageLocatorOptions { HasText = guestName });
        await row.WaitForAsync();
        await row.GetByRole(AriaRole.Button, new() { Name = $"Ações para {guestName}" }).ClickAsync();

        // The reservation is cancelled behind the scenes through the real API while this
        // still-open, now-stale row menu keeps showing "Cancelar reserva" as if it were valid.
        var cancelResponse = await page.Context.APIRequest.PostAsync(
            _fixture.ApiBaseUrl + $"/api/v1/reservations/{reservationId}/cancel",
            new APIRequestContextOptions { Headers = new Dictionary<string, string> { ["Authorization"] = token } });
        cancelResponse.Ok.Should().BeTrue($"test-data setup via the real API must succeed (status {cancelResponse.Status})");

        await page.GetByRole(AriaRole.Menuitem, new() { Name = "Cancelar reserva" }).ClickAsync();
        await page.GetByRole(AriaRole.Heading, new() { Name = "Cancelar reserva" }).WaitForAsync();
        await page.GetByRole(AriaRole.Dialog).GetByRole(AriaRole.Button, new() { Name = "Cancelar reserva" }).ClickAsync();

        await page.GetByText("Não foi possível concluir a ação devido a um conflito (agenda, reserva já cancelada ou edição concorrente).").WaitForAsync();
    }
}
