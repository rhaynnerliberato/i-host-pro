using FluentAssertions;
using Microsoft.Playwright;

namespace IHostPro.Web.Tests.E2E;

/// <summary>
/// Real-browser coverage for the rest of Fase 4 Incremento 2 (Administração
/// de Usuários) — listagem, criação, edição, bloqueio/desbloqueio, atribuição
/// e remoção de papel, e redefinição de senha — once the authorization gate
/// (<see cref="UsersAuthorizationE2ETests"/>) was confirmed green. Drives the
/// real, unmodified <c>IHostPro.Web</c> against the real, unmodified
/// <c>IHostPro.Api</c> — see <see cref="WebE2EFixture"/>. Every test is
/// self-contained: it creates its own throwaway user directly through the
/// real API (using the ADMIN's own real bearer token, captured off a real
/// network request — never fabricated), then drives only the one UI flow
/// under test through the real Angular app.
/// </summary>
[Collection(WebE2EFixtureCollection.Name)]
public sealed class UsersManagementE2ETests
{
    private readonly WebE2EFixture _fixture;

    public UsersManagementE2ETests(WebE2EFixture fixture) => _fixture = fixture;

    private async Task<IPage> NewPageAsync()
    {
        var context = await _fixture.Browser.NewContextAsync();
        return await context.NewPageAsync();
    }

    /// <summary>Logs in as ADMIN and returns the page positioned on "/users", plus the real bearer token the app itself used for GET /api/v1/users/me — for API-level test-data setup only, never for driving the UI.</summary>
    private async Task<(IPage Page, string BearerToken)> LoginAsAdminOnUsersPageAsync()
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
        await page.GetByRole(AriaRole.Link, new() { Name = "Usuários" }).ClickAsync();
        await page.WaitForURLAsync(_fixture.WebBaseUrl + "/users");

        return (page, bearerToken);
    }

    /// <summary>Creates a throwaway user directly through the real API — fast, reliable test-data setup that never substitutes for the real UI flow being tested elsewhere in this file. Returns the created user's id for callers that need it (e.g. to assign a second role).</summary>
    private async Task<string> CreateUserViaApiAsync(IPage page, string bearerToken, string fullName, string email, string roleCode)
    {
        var response = await page.Context.APIRequest.PostAsync(
            _fixture.ApiBaseUrl + "/api/v1/users",
            new APIRequestContextOptions
            {
                Headers = new Dictionary<string, string> { ["Authorization"] = bearerToken },
                DataObject = new { fullName, email, initialPassword = "Correct-Horse-Battery-Staple-99!", roleCode },
            });
        response.Ok.Should().BeTrue($"test-data setup via the real API must succeed (status {response.Status})");
        var body = await response.JsonAsync();
        return body!.Value.GetProperty("id").GetString()!;
    }

    /// <summary>Assigns a second role directly through the real API — used only where a test needs a user with more than one role as valid starting data (e.g. removing a role requires at least one remaining afterward, per RemoveRoleCommandHandler's UserMustHaveAtLeastOneRole rule).</summary>
    private async Task AssignRoleViaApiAsync(IPage page, string bearerToken, string userId, string roleCode)
    {
        var response = await page.Context.APIRequest.PostAsync(
            _fixture.ApiBaseUrl + $"/api/v1/users/{userId}/roles",
            new APIRequestContextOptions
            {
                Headers = new Dictionary<string, string> { ["Authorization"] = bearerToken },
                DataObject = new { roleCode },
            });
        response.Ok.Should().BeTrue($"test-data setup via the real API must succeed (status {response.Status})");
    }

    [Fact]
    public async Task Users_list_shows_the_real_seeded_ADMIN_and_OPERATOR_users()
    {
        var (page, _) = await LoginAsAdminOnUsersPageAsync();

        var table = page.Locator("table");
        await table.GetByText(WebE2EFixture.AdminFullName).WaitForAsync();
        (await table.GetByText(WebE2EFixture.AdminFullName).CountAsync()).Should().BeGreaterThan(0);
        (await table.GetByText(WebE2EFixture.OperatorFullName).CountAsync()).Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Creating_a_user_through_the_dialog_shows_it_in_the_list_with_a_success_message()
    {
        var (page, _) = await LoginAsAdminOnUsersPageAsync();
        var email = $"created-{Guid.NewGuid():N}@e2e-playwright.test";
        const string fullName = "E2E Created User";

        await page.GetByRole(AriaRole.Button, new() { Name = "Novo usuário" }).ClickAsync();
        await page.GetByLabel("Nome completo").FillAsync(fullName);
        await page.GetByLabel("E-mail").FillAsync(email);
        await page.GetByLabel("Senha inicial").FillAsync("Correct-Horse-Battery-Staple-99!");
        await page.GetByLabel("Papel inicial").ClickAsync();
        await page.GetByRole(AriaRole.Option, new() { Name = "Operador" }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Salvar" }).ClickAsync();

        await page.GetByText("Usuário criado com sucesso.").WaitForAsync();
        await page.Locator("table").GetByText(fullName).WaitForAsync();
    }

    [Fact]
    public async Task Editing_a_user_updates_the_displayed_name()
    {
        var (page, token) = await LoginAsAdminOnUsersPageAsync();
        var email = $"edit-{Guid.NewGuid():N}@e2e-playwright.test";
        const string originalName = "E2E Editable User";
        const string updatedName = "E2E Edited User";
        await CreateUserViaApiAsync(page, token, originalName, email, "OPERATOR");
        await page.ReloadAsync();
        await page.Locator("table").GetByText(originalName).WaitForAsync();

        var row = page.Locator("tr", new PageLocatorOptions { HasText = originalName });
        await row.GetByRole(AriaRole.Button, new() { Name = $"Ações para {originalName}" }).ClickAsync();
        await page.GetByRole(AriaRole.Menuitem, new() { Name = "Editar" }).ClickAsync();

        var nameField = page.GetByLabel("Nome completo");
        await nameField.FillAsync(string.Empty);
        await nameField.FillAsync(updatedName);
        await page.GetByRole(AriaRole.Button, new() { Name = "Salvar" }).ClickAsync();

        await page.GetByText("Usuário atualizado com sucesso.").WaitForAsync();
        await page.Locator("table").GetByText(updatedName).WaitForAsync();
    }

    [Fact]
    public async Task Blocking_a_user_requires_confirmation_and_updates_the_status_column()
    {
        var (page, token) = await LoginAsAdminOnUsersPageAsync();
        var email = $"block-{Guid.NewGuid():N}@e2e-playwright.test";
        const string fullName = "E2E Blockable User";
        await CreateUserViaApiAsync(page, token, fullName, email, "OPERATOR");
        await page.ReloadAsync();
        await page.Locator("table").GetByText(fullName).WaitForAsync();

        var row = page.Locator("tr", new PageLocatorOptions { HasText = fullName });
        await row.GetByRole(AriaRole.Button, new() { Name = $"Ações para {fullName}" }).ClickAsync();
        await page.GetByRole(AriaRole.Menuitem, new() { Name = "Bloquear" }).ClickAsync();

        await page.GetByRole(AriaRole.Heading, new() { Name = "Bloquear usuário" }).WaitForAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Bloquear" }).ClickAsync();

        await page.GetByText("Usuário bloqueado com sucesso.").WaitForAsync();
        await row.GetByText("Bloqueado").WaitForAsync();
    }

    [Fact]
    public async Task Unblocking_a_blocked_user_needs_no_confirmation_and_restores_active_status()
    {
        var (page, token) = await LoginAsAdminOnUsersPageAsync();
        var email = $"unblock-{Guid.NewGuid():N}@e2e-playwright.test";
        const string fullName = "E2E Unblockable User";
        await CreateUserViaApiAsync(page, token, fullName, email, "OPERATOR");
        await page.ReloadAsync();
        var row = page.Locator("tr", new PageLocatorOptions { HasText = fullName });
        await row.WaitForAsync();
        await row.GetByRole(AriaRole.Button, new() { Name = $"Ações para {fullName}" }).ClickAsync();
        await page.GetByRole(AriaRole.Menuitem, new() { Name = "Bloquear" }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Bloquear" }).ClickAsync();
        await row.GetByText("Bloqueado").WaitForAsync();

        await row.GetByRole(AriaRole.Button, new() { Name = $"Ações para {fullName}" }).ClickAsync();
        await page.GetByRole(AriaRole.Menuitem, new() { Name = "Desbloquear" }).ClickAsync();

        await page.GetByText("Usuário desbloqueado com sucesso.").WaitForAsync();
        await row.GetByText("Ativo").WaitForAsync();
    }

    [Fact]
    public async Task Assigning_a_role_requires_no_confirmation_and_shows_the_new_role_chip()
    {
        var (page, token) = await LoginAsAdminOnUsersPageAsync();
        var email = $"assignrole-{Guid.NewGuid():N}@e2e-playwright.test";
        const string fullName = "E2E Role Assignee";
        await CreateUserViaApiAsync(page, token, fullName, email, "OPERATOR");
        await page.ReloadAsync();
        var row = page.Locator("tr", new PageLocatorOptions { HasText = fullName });
        await row.WaitForAsync();
        await row.GetByRole(AriaRole.Button, new() { Name = $"Ações para {fullName}" }).ClickAsync();
        await page.GetByRole(AriaRole.Menuitem, new() { Name = "Gerenciar papéis" }).ClickAsync();

        await page.GetByRole(AriaRole.Heading, new() { Name = $"Papéis de {fullName}" }).WaitForAsync();
        await page.GetByLabel("Adicionar papel").ClickAsync();
        await page.GetByRole(AriaRole.Option, new() { Name = "Faxineira" }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Atribuir" }).ClickAsync();

        await page.GetByText("Papel atribuído com sucesso.").WaitForAsync();
        await page.GetByText("Faxineira").WaitForAsync();
    }

    [Fact]
    public async Task Removing_a_role_requires_confirmation_and_removes_the_role_chip()
    {
        var (page, token) = await LoginAsAdminOnUsersPageAsync();
        var email = $"removerole-{Guid.NewGuid():N}@e2e-playwright.test";
        const string fullName = "E2E Role Removal Target";
        // Two roles: RemoveRoleCommandHandler rejects removing a user's last remaining role
        // (UserMustHaveAtLeastOneRole), so a single-role user is invalid data for this flow.
        var userId = await CreateUserViaApiAsync(page, token, fullName, email, "OPERATOR");
        await AssignRoleViaApiAsync(page, token, userId, "HOUSEKEEPER");
        await page.ReloadAsync();
        var row = page.Locator("tr", new PageLocatorOptions { HasText = fullName });
        await row.WaitForAsync();
        await row.GetByRole(AriaRole.Button, new() { Name = $"Ações para {fullName}" }).ClickAsync();
        await page.GetByRole(AriaRole.Menuitem, new() { Name = "Gerenciar papéis" }).ClickAsync();
        await page.GetByRole(AriaRole.Heading, new() { Name = $"Papéis de {fullName}" }).WaitForAsync();

        // Scoped to the open dialog specifically — the underlying users table (still in the
        // DOM behind the modal) renders its own read-only role chips per row, so an unscoped
        // "mat-chip:has-text('Operador')" locator would also match other users' table rows.
        var dialog = page.GetByRole(AriaRole.Dialog);
        var operatorChip = dialog.Locator("mat-chip", new LocatorLocatorOptions { HasText = "Operador" });
        await operatorChip.GetByRole(AriaRole.Button, new() { Name = "Remover papel" }).ClickAsync();
        await page.GetByRole(AriaRole.Heading, new() { Name = "Remover papel" }).WaitForAsync();
        // "Remover" (Exact) disambiguates from the chip's own "Remover papel" remove-icon
        // button, which Playwright's default substring name matching would also match.
        await page.GetByRole(AriaRole.Button, new() { Name = "Remover", Exact = true }).ClickAsync();

        await page.GetByText("Papel removido com sucesso.").WaitForAsync();
        (await operatorChip.CountAsync()).Should().Be(0, "the removed role's chip must disappear");
        // Scoped to the dialog — the underlying table row for this same user also shows a
        // "Faxineira" chip once the list refreshes, which an unscoped locator would also match.
        await dialog.GetByText("Faxineira").WaitForAsync();
    }

    [Fact]
    public async Task Resetting_a_users_password_shows_a_success_message()
    {
        var (page, token) = await LoginAsAdminOnUsersPageAsync();
        var email = $"resetpwd-{Guid.NewGuid():N}@e2e-playwright.test";
        const string fullName = "E2E Password Reset Target";
        await CreateUserViaApiAsync(page, token, fullName, email, "OPERATOR");
        await page.ReloadAsync();
        var row = page.Locator("tr", new PageLocatorOptions { HasText = fullName });
        await row.WaitForAsync();
        await row.GetByRole(AriaRole.Button, new() { Name = $"Ações para {fullName}" }).ClickAsync();
        await page.GetByRole(AriaRole.Menuitem, new() { Name = "Redefinir senha" }).ClickAsync();

        await page.GetByLabel("Nova senha").FillAsync("A-Brand-New-Password-2026!");
        await page.GetByRole(AriaRole.Button, new() { Name = "Redefinir senha" }).ClickAsync();

        await page.GetByText("Senha redefinida com sucesso.").WaitForAsync();
    }

    [Fact]
    public async Task Submitting_the_create_form_twice_quickly_only_creates_the_user_once()
    {
        var (page, _) = await LoginAsAdminOnUsersPageAsync();
        var email = $"noduplicate-{Guid.NewGuid():N}@e2e-playwright.test";
        const string fullName = "E2E No Duplicate Submit";

        await page.GetByRole(AriaRole.Button, new() { Name = "Novo usuário" }).ClickAsync();
        await page.GetByLabel("Nome completo").FillAsync(fullName);
        await page.GetByLabel("E-mail").FillAsync(email);
        await page.GetByLabel("Senha inicial").FillAsync("Correct-Horse-Battery-Staple-99!");
        await page.GetByLabel("Papel inicial").ClickAsync();
        await page.GetByRole(AriaRole.Option, new() { Name = "Operador" }).ClickAsync();

        var saveButton = page.GetByRole(AriaRole.Button, new() { Name = "Salvar" });
        await saveButton.ClickAsync();
        // The button becomes disabled (submitting signal) the instant the first click fires,
        // so this second click — issued without waiting for the request to complete — must be a no-op.
        await saveButton.ClickAsync(new LocatorClickOptions { Force = true, Timeout = 2000 }).ContinueWith(_ => { });

        await page.GetByText("Usuário criado com sucesso.").WaitForAsync();
        // The snackbar fires before loadUsers()'s HTTP round-trip resolves, so wait for the
        // row itself (not just the snackbar) before counting — otherwise this can race the
        // still-in-flight reload and see zero matches even though creation succeeded.
        var tableRows = page.Locator("table").GetByText(fullName);
        await tableRows.First.WaitForAsync();
        (await tableRows.CountAsync()).Should().Be(1, "a duplicate double-click must never create the same user twice");
    }
}
