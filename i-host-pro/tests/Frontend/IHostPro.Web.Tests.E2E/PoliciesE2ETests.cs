using FluentAssertions;
using Microsoft.Playwright;

namespace IHostPro.Web.Tests.E2E;

/// <summary>
/// Real-browser coverage for Fase 5, Incremento 1, Checkpoint 5 (Policy
/// Engine Foundation — administrative frontend), once the authorization gate
/// (<see cref="PoliciesAuthorizationE2ETests"/>) was confirmed green. Drives
/// the real, unmodified <c>IHostPro.Web</c> against the real, unmodified
/// <c>IHostPro.Api</c> — see <see cref="WebE2EFixture"/>.
///
/// <c>PolicyDefinition</c> (<c>EARLY_CHECKIN</c>/<c>LATE_CHECKOUT</c>) is a
/// system catalog seeded once via EF Core migration data, never created
/// through this UI — every test here exercises only <c>PolicyValue</c>
/// creation/reading, which is the sole write surface Checkpoint 4's API (and
/// this checkpoint's UI) exposes. <c>PolicyScopeParser</c> never validates
/// that a Property-scope <c>propertyId</c> actually exists in
/// PropertyManagement (Configuration.Contracts carries zero project
/// references, by design) — so a fresh random GUID is a valid, sufficient
/// Property-scope key for these tests, with no PropertyManagement API
/// dependency needed.
/// </summary>
[Collection(WebE2EFixtureCollection.Name)]
public sealed class PoliciesE2ETests
{
    private readonly WebE2EFixture _fixture;

    public PoliciesE2ETests(WebE2EFixture fixture) => _fixture = fixture;

    private async Task<IPage> NewPageAsync()
    {
        var context = await _fixture.Browser.NewContextAsync();
        return await context.NewPageAsync();
    }

    /// <summary>
    /// Logs in as the dedicated <see cref="WebE2EFixture.PolicyAdminEmail"/>
    /// persona (holds both POLICIES:READ and POLICIES:MANAGE — see its own
    /// doc comment for why the standard Admin persona, MANAGE-only, cannot
    /// be used here) and returns the page positioned on /policies.
    /// </summary>
    private async Task<IPage> LoginAsAdminOnPoliciesAsync()
    {
        var page = await NewPageAsync();
        await page.GotoAsync(_fixture.WebBaseUrl + "/login");
        await page.GetByLabel("Empresa").FillAsync(WebE2EFixture.TenantSlugValue);
        await page.GetByLabel("E-mail").FillAsync(WebE2EFixture.PolicyAdminEmail);
        await page.GetByLabel("Senha").FillAsync(WebE2EFixture.PolicyAdminPassword);
        await page.GetByRole(AriaRole.Button, new() { Name = "Entrar" }).ClickAsync();

        await page.WaitForURLAsync(_fixture.WebBaseUrl + "/");
        await page.GetByRole(AriaRole.Link, new() { Name = "Políticas" }).ClickAsync();
        await page.WaitForURLAsync(_fixture.WebBaseUrl + "/policies");

        return page;
    }

    private static async Task OpenDetailDialogAsync(IPage page, string policyName)
    {
        var row = page.Locator("tr", new PageLocatorOptions { HasText = policyName });
        await row.WaitForAsync();
        await row.GetByRole(AriaRole.Button, new() { Name = "Gerenciar" }).ClickAsync();
        await page.GetByRole(AriaRole.Heading, new() { Name = policyName }).WaitForAsync();
    }

    /// <summary>
    /// Clicks "Carregar" and waits for the load to actually finish (the
    /// spinner <c>PolicyDetailDialog</c> shows while <c>loading()</c> is true
    /// to disappear) before returning. Required, not optional: unlike
    /// <c>WaitForAsync</c> on text/element visibility, Playwright's
    /// <c>CountAsync</c> never auto-waits for anything — a caller that counts
    /// history rows right after this returns, with no wait of its own, would
    /// otherwise race the async HTTP fetch <c>load()</c> just triggered and
    /// could see a not-yet-populated (zero-row) table (Checkpoint 7
    /// homologação, real defect found and fixed the first time a test here
    /// actually depended on the row count reflecting reality at the instant
    /// it was read).
    /// </summary>
    private static async Task LoadTenantScopeAsync(IPage page)
    {
        await page.GetByRole(AriaRole.Button, new() { Name = "Carregar" }).ClickAsync();
        await page.Locator("mat-progress-spinner").WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Detached });
    }

    private static async Task LoadPropertyScopeAsync(IPage page, string propertyId)
    {
        await page.GetByLabel("Escopo").ClickAsync();
        await page.GetByRole(AriaRole.Option, new() { Name = "Imóvel específico" }).ClickAsync();
        await page.GetByLabel("ID do imóvel").FillAsync(propertyId);
        await page.GetByRole(AriaRole.Button, new() { Name = "Carregar" }).ClickAsync();
        await page.Locator("mat-progress-spinner").WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Detached });
    }

    [Fact]
    public async Task Admin_views_the_policy_catalog()
    {
        var page = await LoginAsAdminOnPoliciesAsync();

        await page.Locator("table").GetByText("EARLY_CHECKIN").WaitForAsync();
        await page.Locator("table").GetByText("Early Check-in").WaitForAsync();
        await page.Locator("table").GetByText("LATE_CHECKOUT").WaitForAsync();
        await page.Locator("table").GetByText("Late Checkout").WaitForAsync();
    }

    [Fact]
    public async Task Admin_creates_the_first_version_of_EARLY_CHECKIN_at_Tenant_scope()
    {
        var page = await LoginAsAdminOnPoliciesAsync();
        await OpenDetailDialogAsync(page, "Early Check-in");

        await LoadTenantScopeAsync(page);

        // Not "Não há valor configurado" / "Versão 1": PoliciesE2ETests shares one WebE2EFixture
        // tenant across every test method in this class, and xUnit does not guarantee method
        // execution order — Admin_creates_a_Property_scoped_version_independent_of_the_Tenant_scope
        // and Admin_sees_two_versions_in_history_after_editing_a_configured_value both also write
        // an EARLY_CHECKIN/Tenant version, so this may not actually be the first one whenever this
        // test happens to run (Checkpoint 7 homologação, real cross-test ordering gap found the
        // first time this suite ever ran for real). The reason text is unique per test and is what
        // actually proves this specific write landed, regardless of which version number it got.
        await page.GetByLabel("Motivo").FillAsync("Configuração inicial via E2E");
        await page.GetByRole(AriaRole.Checkbox, new() { Name = "Permitido" }).ClickAsync();
        await page.GetByLabel("Horário mais cedo").FillAsync("13:30");
        await page.GetByRole(AriaRole.Button, new() { Name = "Salvar nova versão" }).ClickAsync();

        await page.GetByText("Nova versão criada com sucesso.").WaitForAsync();
        var historyTable = page.Locator("table", new PageLocatorOptions { HasText = "Motivo" });
        await historyTable.GetByText("Configuração inicial via E2E").WaitForAsync();
    }

    [Fact]
    public async Task Admin_creates_a_Property_scoped_version_independent_of_the_Tenant_scope()
    {
        var page = await LoginAsAdminOnPoliciesAsync();
        await OpenDetailDialogAsync(page, "Early Check-in");
        var propertyId = Guid.NewGuid().ToString();

        // A Tenant-scope version first, so the two scopes' independence is actually observable.
        await LoadTenantScopeAsync(page);
        await page.GetByLabel("Motivo").FillAsync("Regra padrão da empresa");
        await page.GetByRole(AriaRole.Button, new() { Name = "Salvar nova versão" }).ClickAsync();
        await page.GetByText("Nova versão criada com sucesso.").WaitForAsync();

        await LoadPropertyScopeAsync(page, propertyId);
        await page.GetByText("Não há valor configurado atualmente neste escopo.").WaitForAsync();

        await page.GetByLabel("Motivo").FillAsync("Regra específica do imóvel via E2E");
        await page.GetByRole(AriaRole.Checkbox, new() { Name = "Permitido" }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Salvar nova versão" }).ClickAsync();
        await page.GetByText("Nova versão criada com sucesso.").WaitForAsync();
        await page.GetByText("Definido neste nível.").WaitForAsync();

        // Reloading the Tenant scope must still show its own separate current value — the reason
        // text (unique to this test) is what proves that, not an absolute "Versão 1": this class
        // shares one WebE2EFixture tenant across every test method, so another test's own
        // EARLY_CHECKIN/Tenant write may have already advanced the version by the time this runs
        // (see Admin_creates_the_first_version_of_EARLY_CHECKIN_at_Tenant_scope's own comment).
        await page.GetByLabel("Escopo").ClickAsync();
        await page.GetByRole(AriaRole.Option, new() { Name = "Empresa (toda a conta)" }).ClickAsync();
        await LoadTenantScopeAsync(page);
        var tenantHistoryTable = page.Locator("table", new PageLocatorOptions { HasText = "Motivo" });
        await tenantHistoryTable.GetByText("Regra padrão da empresa").WaitForAsync();
    }

    [Fact]
    public async Task Admin_sees_two_versions_in_history_after_editing_a_configured_value()
    {
        var page = await LoginAsAdminOnPoliciesAsync();
        await OpenDetailDialogAsync(page, "Early Check-in");

        await LoadTenantScopeAsync(page);

        await page.GetByLabel("Motivo").FillAsync("Versão inicial via E2E");
        await page.GetByRole(AriaRole.Button, new() { Name = "Salvar nova versão" }).ClickAsync();
        await page.GetByText("Nova versão criada com sucesso.").WaitForAsync();

        // The form is repopulated from the just-created current value; only the reason must change to author the next version.
        var reasonField = page.GetByLabel("Motivo");
        await reasonField.FillAsync(string.Empty);
        await reasonField.FillAsync("Ajuste de regra via E2E");
        await page.GetByRole(AriaRole.Button, new() { Name = "Salvar nova versão" }).ClickAsync();
        await page.GetByText("Nova versão criada com sucesso.").WaitForAsync();

        // Not a row-count assertion: PoliciesE2ETests shares one WebE2EFixture tenant across every
        // test method, and other tests also write EARLY_CHECKIN/Tenant versions — Playwright's
        // CountAsync never auto-waits, so any count captured at a specific instant (a "baseline
        // before this test's writes") can still race a concurrently-settling fetch from another
        // test's own reload, exactly what an intermittent CI run of this exact test caught (found
        // during Checkpoint 7 homologação, both a real WebE2EFixture cross-test-timing gap and a
        // fragile test design in the same discovery). Checking each specific row directly — both
        // reasons present, and specifically which one is marked current — proves "both the
        // superseded and the current version remain listed in history" without depending on the
        // total row count at all, however many other rows any other test's writes may have added.
        // tr[mat-row] (never "tbody tr"): the directive's own attribute selector, present verbatim
        // in the rendered DOM, excludes the header row without depending on Material's <thead>/<tbody>
        // wrapping behavior.
        var historyTable = page.Locator("table", new PageLocatorOptions { HasText = "Motivo" });
        var initialRow = historyTable.Locator("tr[mat-row]", new LocatorLocatorOptions { HasText = "Versão inicial via E2E" });
        var adjustedRow = historyTable.Locator("tr[mat-row]", new LocatorLocatorOptions { HasText = "Ajuste de regra via E2E" });
        await initialRow.WaitForAsync();
        await adjustedRow.WaitForAsync();
        (await initialRow.GetByText("✓").CountAsync()).Should().Be(0, "the superseded version must no longer be marked current");
        await adjustedRow.GetByText("✓").WaitForAsync();
    }

    [Fact]
    public async Task Admin_manages_LATE_CHECKOUT_with_a_percentage_charge()
    {
        var page = await LoginAsAdminOnPoliciesAsync();
        await OpenDetailDialogAsync(page, "Late Checkout");

        await LoadTenantScopeAsync(page);
        await page.GetByLabel("Motivo").FillAsync("Cobrança percentual via E2E");
        await page.GetByRole(AriaRole.Checkbox, new() { Name = "Permitido" }).ClickAsync();
        await page.GetByLabel("Horário mais tarde").FillAsync("15:00");
        await page.GetByLabel("Tipo de cobrança").ClickAsync();
        await page.GetByRole(AriaRole.Option, new() { Name = "Percentual" }).ClickAsync();
        await page.GetByLabel("Valor da cobrança").FillAsync("10.5");
        await page.GetByRole(AriaRole.Button, new() { Name = "Salvar nova versão" }).ClickAsync();

        await page.GetByText("Nova versão criada com sucesso.").WaitForAsync();
        await page.GetByText("Versão 1").First.WaitForAsync();
    }
}
