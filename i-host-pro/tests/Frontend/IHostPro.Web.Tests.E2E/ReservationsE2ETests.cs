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

    /// <summary>
    /// UpdateReservationCommandHandler reads a read-only snapshot (with its xmin) BEFORE its write
    /// transaction opens, then re-checks that xmin from inside the transaction — if a second PATCH
    /// commits in between, the second one's own re-check (or, for the narrower window between that
    /// re-check and the eventual SaveChanges, EF Core's own xmin-as-concurrency-token check) finds
    /// a mismatch and returns ReservationConcurrencyConflict, with no automatic retry.
    ///
    /// Rebuilt to fire two genuinely concurrent PATCH requests directly against the real API
    /// (Task.WhenAll on two independent ADMIN sessions' own <c>APIRequestContext</c>s), not through
    /// two browser clicks: a UI-driven version of this test occasionally produced two HTTP 200s in
    /// this session's own investigation, and root-causing that showed it could not be distinguished
    /// from "the two requests simply never overlapped at the server" (Task.WhenAll on
    /// ClickAsync only guarantees the clicks are dispatched close together, not that the resulting
    /// server-side processing overlaps) — a pure client-side timing artifact, not evidence either
    /// way about the handler itself. Firing the two HTTP calls directly, from the same process,
    /// removes that entire source of ambiguity. Runs several independent iterations (fresh
    /// reservation each time) to check for stability, per instruction — no retry-until-green, no
    /// weakened assertion: any iteration that fails, fails the test.
    ///
    /// The API intentionally collapses every conflict code (ReservationDateConflict,
    /// ReservationAlreadyCancelled, CancelledReservationCannotBeModified,
    /// ReservationConcurrencyConflict) to the same generic 409 ProblemDetails with no `codes`
    /// extension (see ReservationsResultHttpMapper — unlike its own 400 branch, the 409 branch
    /// never adds one), so which specific code produced a given 409 cannot be read off the HTTP
    /// response body. This test scenario rules out the other three by construction instead: the
    /// PATCH changes only guestName (check-in/check-out never change, so no date-overlap check can
    /// fire — HasConflictingReservationAsync also excludes the reservation's own id), and the
    /// reservation is freshly created and never cancelled by anything in this test. A 409 here can
    /// therefore only be ReservationConcurrencyConflict.
    /// </summary>
    [Fact]
    public async Task A_concurrency_conflict_is_presented_without_automatic_retry()
    {
        var (page, tokenA) = await LoginAsAdminOnReservationsAsync();
        var (_, tokenB) = await LoginAsAdminOnReservationsAsync();
        var propertyId = await CreateActivePropertyViaApiAsync(page, tokenA, "E2E-RES-CONCURRENCY-1", "E2E Reservation Concurrency Property");

        for (var iteration = 1; iteration <= 5; iteration++)
        {
            // Each iteration uses its own non-overlapping date range on the same property — a
            // fresh reservation per race, never colliding with an earlier iteration's (already
            // guestName-modified, but still date-unchanged) reservation via ReservationDateConflict.
            var checkIn = new DateTimeOffset(2026, 10, 1, 14, 0, 0, TimeSpan.Zero).AddDays((iteration - 1) * 3);
            var checkOut = checkIn.AddDays(2).AddHours(-3);
            var reservationId = await CreateReservationViaApiAsync(
                page, tokenA, propertyId, $"E2E Concurrency Guest {iteration}", null,
                checkIn.ToString("O"), checkOut.ToString("O"), 2);

            // Both sessions read the same starting version before either writes.
            var readByA = await GetReservationViaApiAsync(page, tokenA, reservationId);
            var readByB = await GetReservationViaApiAsync(page, tokenB, reservationId);
            readByA.GetProperty("guestName").GetString().Should().Be(readByB.GetProperty("guestName").GetString());

            var patchTaskA = PatchGuestNameAsync(page, tokenA, reservationId, $"Concurrent Edit A {iteration}");
            var patchTaskB = PatchGuestNameAsync(page, tokenB, reservationId, $"Concurrent Edit B {iteration}");
            var (responseA, responseB) = (await patchTaskA, await patchTaskB);

            var succeededA = responseA.Status == 200;
            var succeededB = responseB.Status == 200;
            (succeededA ^ succeededB).Should().BeTrue(
                $"iteration {iteration}: exactly one of two genuinely concurrent PATCHes on the same reservation must succeed (200) and the other must be rejected with a conflict (409) — got statusA={responseA.Status}, statusB={responseB.Status}. Never both succeeding (silently overwriting each other) and never both failing.");

            var loserStatus = succeededA ? responseB.Status : responseA.Status;
            loserStatus.Should().Be(409, $"iteration {iteration}: the loser must be rejected with a conflict, not any other error");

            var winningName = succeededA ? $"Concurrent Edit A {iteration}" : $"Concurrent Edit B {iteration}";
            var finalState = await GetReservationViaApiAsync(page, tokenA, reservationId);
            finalState.GetProperty("guestName").GetString().Should().Be(winningName,
                $"iteration {iteration}: only the winning PATCH's change must be persisted — no silent merge, no partial application of the loser's data");

            var auditEntryCount = await _fixture.CountReservationAuditEntriesAsync(Guid.Parse(reservationId), "reservation_updated");
            auditEntryCount.Should().Be(1,
                $"iteration {iteration}: exactly one reservation_updated audit entry must exist — the losing request must have produced neither an audit entry nor an event (both are written atomically with the same transaction that the loser's SaveChanges/explicit xmin check rejects)");
        }
    }

    private async Task<IAPIResponse> PatchGuestNameAsync(IPage page, string bearerToken, string reservationId, string guestName) =>
        await page.Context.APIRequest.PatchAsync(
            _fixture.ApiBaseUrl + $"/api/v1/reservations/{reservationId}",
            new APIRequestContextOptions
            {
                Headers = new Dictionary<string, string> { ["Authorization"] = bearerToken },
                DataObject = new { guestName },
            });

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
