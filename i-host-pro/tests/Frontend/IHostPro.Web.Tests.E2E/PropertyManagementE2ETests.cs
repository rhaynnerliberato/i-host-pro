using FluentAssertions;
using Microsoft.Playwright;

namespace IHostPro.Web.Tests.E2E;

/// <summary>
/// Real-browser coverage for the rest of Fase 4 Incremento 3 (Gestão de
/// Condomínios e Imóveis) — listagem/criação/edição de condomínios,
/// listagem/criação/edição de imóveis, transições de ciclo de vida e
/// vínculo/remoção de proprietários — once the authorization gate
/// (<see cref="PropertyManagementAuthorizationE2ETests"/>) was confirmed
/// green. Drives the real, unmodified <c>IHostPro.Web</c> against the real,
/// unmodified <c>IHostPro.Api</c> — see <see cref="WebE2EFixture"/>. Every
/// test is self-contained: it creates its own throwaway condominium/property/
/// owner directly through the real API (using the ADMIN's own real bearer
/// token, captured off a real network request — never fabricated) whenever
/// the flow under test is not itself the creation flow, then drives only the
/// one UI action under test through the real Angular app.
/// </summary>
[Collection(WebE2EFixtureCollection.Name)]
public sealed class PropertyManagementE2ETests
{
    private readonly WebE2EFixture _fixture;

    public PropertyManagementE2ETests(WebE2EFixture fixture) => _fixture = fixture;

    private async Task<IPage> NewPageAsync()
    {
        var context = await _fixture.Browser.NewContextAsync();
        return await context.NewPageAsync();
    }

    /// <summary>Logs in as ADMIN and returns the page positioned on the given property-management route, plus the real bearer token the app itself used for GET /api/v1/users/me — for API-level test-data setup only, never for driving the UI.</summary>
    private async Task<(IPage Page, string BearerToken)> LoginAsAdminOnAsync(string navLinkName, string route)
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
        await page.GetByRole(AriaRole.Link, new() { Name = navLinkName }).ClickAsync();
        await page.WaitForURLAsync(_fixture.WebBaseUrl + route);

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

    /// <summary>Creates a throwaway condominium directly through the real API. Returns its id.</summary>
    private async Task<string> CreateCondominiumViaApiAsync(IPage page, string bearerToken, string name)
    {
        var response = await page.Context.APIRequest.PostAsync(
            _fixture.ApiBaseUrl + "/api/v1/condominiums",
            new APIRequestContextOptions
            {
                Headers = new Dictionary<string, string> { ["Authorization"] = bearerToken },
                DataObject = new { name, address = SampleAddress },
            });
        response.Ok.Should().BeTrue($"test-data setup via the real API must succeed (status {response.Status})");
        var body = await response.JsonAsync();
        return body!.Value.GetProperty("id").GetString()!;
    }

    /// <summary>Creates a throwaway property directly through the real API, with its own resolvable address so Activate never fails on address resolution unrelated to the flow under test. Returns its id.</summary>
    private async Task<string> CreatePropertyViaApiAsync(IPage page, string bearerToken, string code, string name, string? condominiumId = null)
    {
        var response = await page.Context.APIRequest.PostAsync(
            _fixture.ApiBaseUrl + "/api/v1/properties",
            new APIRequestContextOptions
            {
                Headers = new Dictionary<string, string> { ["Authorization"] = bearerToken },
                DataObject = new { code, name, capacity = 4, condominiumId, address = SampleAddress },
            });
        response.Ok.Should().BeTrue($"test-data setup via the real API must succeed (status {response.Status})");
        var body = await response.JsonAsync();
        return body!.Value.GetProperty("id").GetString()!;
    }

    /// <summary>Creates a throwaway user holding PROPERTY_OWNER directly through the real API — the only role LinkPropertyOwnerCommandHandler's eligibility check (IIdentityUserEligibilityReader) accepts for this feature. Returns its id.</summary>
    private async Task<string> CreateEligibleOwnerUserViaApiAsync(IPage page, string bearerToken)
    {
        var response = await page.Context.APIRequest.PostAsync(
            _fixture.ApiBaseUrl + "/api/v1/users",
            new APIRequestContextOptions
            {
                Headers = new Dictionary<string, string> { ["Authorization"] = bearerToken },
                DataObject = new
                {
                    fullName = "E2E Property Owner",
                    email = $"owner-{Guid.NewGuid():N}@e2e-playwright.test",
                    initialPassword = "Correct-Horse-Battery-Staple-99!",
                    roleCode = "PROPERTY_OWNER",
                },
            });
        response.Ok.Should().BeTrue($"test-data setup via the real API must succeed (status {response.Status})");
        var body = await response.JsonAsync();
        return body!.Value.GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task Admin_lists_and_creates_a_condominium()
    {
        var (page, _) = await LoginAsAdminOnAsync("Condomínios", "/condominiums");
        const string name = "E2E Edificio Sol";

        await page.GetByRole(AriaRole.Button, new() { Name = "Novo condomínio" }).ClickAsync();
        await page.GetByLabel("Nome").FillAsync(name);
        await page.GetByLabel("CEP").FillAsync("01000-000");
        await page.GetByLabel("Rua").FillAsync("Rua das Flores");
        await page.GetByLabel("Número").FillAsync("100");
        await page.GetByLabel("Bairro").FillAsync("Centro");
        await page.GetByLabel("Cidade").FillAsync("Sao Paulo");
        await page.GetByLabel("Estado").FillAsync("SP");
        await page.GetByRole(AriaRole.Button, new() { Name = "Salvar" }).ClickAsync();

        await page.GetByText("Condomínio criado com sucesso.").WaitForAsync();
        await page.Locator("table").GetByText(name).WaitForAsync();
    }

    [Fact]
    public async Task Admin_edits_a_condominium()
    {
        var (page, token) = await LoginAsAdminOnAsync("Condomínios", "/condominiums");
        const string originalName = "E2E Editable Condominium";
        const string updatedName = "E2E Edited Condominium";
        await CreateCondominiumViaApiAsync(page, token, originalName);
        await page.ReloadAsync();
        await page.Locator("table").GetByText(originalName).WaitForAsync();

        var row = page.Locator("tr", new PageLocatorOptions { HasText = originalName });
        await row.GetByRole(AriaRole.Button, new() { Name = "Editar" }).ClickAsync();

        var nameField = page.GetByLabel("Nome");
        await nameField.FillAsync(string.Empty);
        await nameField.FillAsync(updatedName);
        await page.GetByRole(AriaRole.Button, new() { Name = "Salvar" }).ClickAsync();

        await page.GetByText("Condomínio atualizado com sucesso.").WaitForAsync();
        await page.Locator("table").GetByText(updatedName).WaitForAsync();
    }

    [Fact]
    public async Task Admin_creates_a_property_linked_to_a_condominium()
    {
        var (page, token) = await LoginAsAdminOnAsync("Imóveis", "/properties");
        const string condominiumName = "E2E Condominium For Property Link";
        await CreateCondominiumViaApiAsync(page, token, condominiumName);
        await page.ReloadAsync();
        const string code = "E2E-APT-1";
        const string name = "E2E Apto Vinculado";

        await page.GetByRole(AriaRole.Button, new() { Name = "Novo imóvel" }).ClickAsync();
        await page.GetByLabel("Código").FillAsync(code);
        await page.GetByLabel("Nome").FillAsync(name);
        await page.GetByLabel("Capacidade").FillAsync("2");
        // Exact: "Condomínio" is otherwise a substring match of the "Endereço próprio
        // (diferente do condomínio)" checkbox's own accessible name.
        await page.GetByLabel("Condomínio", new() { Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Option, new() { Name = condominiumName }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Salvar" }).ClickAsync();

        await page.GetByText("Imóvel criado com sucesso.").WaitForAsync();
        var row = page.Locator("tr", new PageLocatorOptions { HasText = name });
        await row.WaitForAsync();
        (await row.GetByText(code).CountAsync()).Should().BeGreaterThan(0, "the created property must show the code entered in the form");
    }

    [Fact]
    public async Task Admin_edits_a_property()
    {
        var (page, token) = await LoginAsAdminOnAsync("Imóveis", "/properties");
        const string originalName = "E2E Editable Property";
        const string updatedName = "E2E Edited Property";
        await CreatePropertyViaApiAsync(page, token, "E2E-EDIT-1", originalName);
        await page.ReloadAsync();
        var row = page.Locator("tr", new PageLocatorOptions { HasText = originalName });
        await row.WaitForAsync();
        await row.GetByRole(AriaRole.Button, new() { Name = $"Ações para {originalName}" }).ClickAsync();
        await page.GetByRole(AriaRole.Menuitem, new() { Name = "Editar" }).ClickAsync();

        var nameField = page.GetByLabel("Nome");
        await nameField.FillAsync(string.Empty);
        await nameField.FillAsync(updatedName);
        await page.GetByRole(AriaRole.Button, new() { Name = "Salvar" }).ClickAsync();

        await page.GetByText("Imóvel atualizado com sucesso.").WaitForAsync();
        await page.Locator("table").GetByText(updatedName).WaitForAsync();
    }

    [Fact]
    public async Task Admin_executes_the_allowed_lifecycle_transitions_in_sequence()
    {
        var (page, token) = await LoginAsAdminOnAsync("Imóveis", "/properties");
        const string name = "E2E Lifecycle Property";
        // Created with its own address, so Activate can resolve an effective address without depending on a condominium link — out of scope for this flow.
        await CreatePropertyViaApiAsync(page, token, "E2E-LIFECYCLE-1", name);
        await page.ReloadAsync();
        var row = page.Locator("tr", new PageLocatorOptions { HasText = name });
        await row.WaitForAsync();
        (await row.GetByText("Rascunho").CountAsync()).Should().BeGreaterThan(0, "a newly-created property starts in Draft");

        // Draft -> Active: only Editar/Gerenciar proprietários/Ativar/Arquivar must be offered.
        await row.GetByRole(AriaRole.Button, new() { Name = $"Ações para {name}" }).ClickAsync();
        var menu = page.GetByRole(AriaRole.Menu);
        (await menu.GetByRole(AriaRole.Menuitem, new() { Name = "Desativar" }).CountAsync()).Should().Be(0, "Deactivate is not valid from Draft");
        await menu.GetByRole(AriaRole.Menuitem, new() { Name = "Ativar" }).ClickAsync();
        await page.GetByText("Imóvel ativado com sucesso.").WaitForAsync();
        await row.GetByText("Ativo").WaitForAsync();

        // Active -> Inactive: only Editar/Gerenciar proprietários/Desativar must be offered.
        await row.GetByRole(AriaRole.Button, new() { Name = $"Ações para {name}" }).ClickAsync();
        menu = page.GetByRole(AriaRole.Menu);
        // Exact: "Ativar" is otherwise a case-insensitive substring match of "Desativar", which
        // is the one item that IS present here — an unscoped/non-exact query would (and did)
        // report a false positive "match" against Desativar's own label.
        (await menu.GetByRole(AriaRole.Menuitem, new() { Name = "Ativar", Exact = true }).CountAsync()).Should().Be(0, "Activate is not valid from Active");
        (await menu.GetByRole(AriaRole.Menuitem, new() { Name = "Arquivar" }).CountAsync()).Should().Be(0, "Archive is not valid from Active");
        await menu.GetByRole(AriaRole.Menuitem, new() { Name = "Desativar" }).ClickAsync();
        await page.GetByText("Imóvel desativado com sucesso.").WaitForAsync();
        await row.GetByText("Inativo").WaitForAsync();

        // Inactive -> Archived (requires confirmation): afterward, Archived is terminal — no Editar.
        await row.GetByRole(AriaRole.Button, new() { Name = $"Ações para {name}" }).ClickAsync();
        await page.GetByRole(AriaRole.Menu).GetByRole(AriaRole.Menuitem, new() { Name = "Arquivar" }).ClickAsync();
        await page.GetByRole(AriaRole.Heading, new() { Name = "Arquivar imóvel" }).WaitForAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Arquivar", Exact = true }).ClickAsync();
        await page.GetByText("Imóvel arquivado com sucesso.").WaitForAsync();
        await row.GetByText("Arquivado").WaitForAsync();

        await row.GetByRole(AriaRole.Button, new() { Name = $"Ações para {name}" }).ClickAsync();
        (await page.GetByRole(AriaRole.Menu).GetByRole(AriaRole.Menuitem, new() { Name = "Editar" }).CountAsync()).Should().Be(0, "an archived property can no longer be edited");
    }

    [Fact]
    public async Task Admin_links_an_owner_to_a_property()
    {
        var (page, token) = await LoginAsAdminOnAsync("Imóveis", "/properties");
        const string name = "E2E Property For Owner Link";
        await CreatePropertyViaApiAsync(page, token, "E2E-OWNER-LINK-1", name);
        var ownerUserId = await CreateEligibleOwnerUserViaApiAsync(page, token);
        await page.ReloadAsync();
        var row = page.Locator("tr", new PageLocatorOptions { HasText = name });
        await row.WaitForAsync();
        await row.GetByRole(AriaRole.Button, new() { Name = $"Ações para {name}" }).ClickAsync();
        await page.GetByRole(AriaRole.Menuitem, new() { Name = "Gerenciar proprietários" }).ClickAsync();

        await page.GetByRole(AriaRole.Heading, new() { Name = $"Proprietários de {name}" }).WaitForAsync();
        await page.GetByText("Nenhum proprietário vinculado.").WaitForAsync();
        await page.GetByLabel("ID do usuário proprietário").FillAsync(ownerUserId);
        await page.GetByRole(AriaRole.Button, new() { Name = "Vincular" }).ClickAsync();

        await page.GetByText("Proprietário vinculado com sucesso.").WaitForAsync();
        await page.GetByRole(AriaRole.Dialog).GetByText(ownerUserId).WaitForAsync();
    }

    [Fact]
    public async Task Admin_removes_an_owner_from_a_property()
    {
        var (page, token) = await LoginAsAdminOnAsync("Imóveis", "/properties");
        const string name = "E2E Property For Owner Removal";
        var propertyId = await CreatePropertyViaApiAsync(page, token, "E2E-OWNER-REMOVE-1", name);
        var ownerUserId = await CreateEligibleOwnerUserViaApiAsync(page, token);
        var linkResponse = await page.Context.APIRequest.PostAsync(
            _fixture.ApiBaseUrl + $"/api/v1/properties/{propertyId}/owners",
            new APIRequestContextOptions
            {
                Headers = new Dictionary<string, string> { ["Authorization"] = token },
                DataObject = new { ownerUserId },
            });
        linkResponse.Ok.Should().BeTrue($"test-data setup via the real API must succeed (status {linkResponse.Status})");
        await page.ReloadAsync();
        var row = page.Locator("tr", new PageLocatorOptions { HasText = name });
        await row.WaitForAsync();
        await row.GetByRole(AriaRole.Button, new() { Name = $"Ações para {name}" }).ClickAsync();
        await page.GetByRole(AriaRole.Menuitem, new() { Name = "Gerenciar proprietários" }).ClickAsync();
        await page.GetByRole(AriaRole.Heading, new() { Name = $"Proprietários de {name}" }).WaitForAsync();
        await page.GetByRole(AriaRole.Dialog).GetByText(ownerUserId).WaitForAsync();

        await page.GetByRole(AriaRole.Button, new() { Name = "Remover" }).ClickAsync();
        await page.GetByRole(AriaRole.Heading, new() { Name = "Remover proprietário" }).WaitForAsync();
        // Scoped to the confirmation dialog specifically: its own confirm button and the
        // per-owner remove icon button both carry the exact accessible name "Remover".
        await page.GetByRole(AriaRole.Dialog).Last.GetByRole(AriaRole.Button, new() { Name = "Remover", Exact = true }).ClickAsync();

        await page.GetByText("Vínculo removido com sucesso.").WaitForAsync();
        await page.GetByText("Nenhum proprietário vinculado.").WaitForAsync();
    }
}
