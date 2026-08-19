using FluentAssertions;
using IHostPro.Contexts.ExternalIntegrations.Application.WhatsAppIntegrations;
using IHostPro.Contexts.ExternalIntegrations.Application.WhatsAppTemplateMappings;
using IHostPro.Contexts.ExternalIntegrations.Contracts;
using IHostPro.Contexts.ExternalIntegrations.Domain;
using IHostPro.Contexts.ExternalIntegrations.Infrastructure;
using IHostPro.Contexts.ExternalIntegrations.Infrastructure.Meta;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit.Abstractions;

namespace IHostPro.Contexts.ExternalIntegrations.Tests.Integration;

/// <summary>
/// Fase 9, Checkpoint 2.2 mandate §54-57: the ONE controlled real-sandbox
/// proof — a genuine HTTP call to Meta's WhatsApp Cloud API, run ONLY when
/// local credentials/configuration are already present (User Secrets on
/// <c>IHostPro.Api</c>'s own secrets store, or environment variables). Never
/// asks for or accepts a secret pasted into this repository — this test only
/// CHECKS for local presence and, when absent, prints the exact
/// <c>dotnet user-secrets</c> commands needed (no values), then passes
/// trivially (a missing sandbox credential is not a code defect and must
/// never block publication — mandate §56).
///
/// When configured, this sends exactly ONE real template message to a
/// locally-configured, already-authorized test recipient — never a real
/// guest/reservation, never Production, never more than one message per run.
/// </summary>
public class MetaWhatsAppSandboxProofTests
{
    private const string ApiUserSecretsId = "dotnet-IHostPro.Api-ffaa4964-a352-4170-8e93-b0b5f8dcf47b";
    private const string SandboxSectionPath = "ExternalIntegrations:WhatsApp:SandboxTest";
    private const string AccessTokenSecretReference = "SandboxAccessToken";

    private readonly ITestOutputHelper _output;

    public MetaWhatsAppSandboxProofTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task SendAsync_against_the_real_Meta_sandbox_when_local_credentials_are_configured()
    {
        var configuration = new ConfigurationBuilder()
            .AddUserSecrets(ApiUserSecretsId)
            .AddEnvironmentVariables()
            .Build();

        var phoneNumberId = configuration[$"{SandboxSectionPath}:PhoneNumberId"];
        var recipient = configuration[$"{SandboxSectionPath}:RecipientPhoneNumber"];
        var providerTemplateName = configuration[$"{SandboxSectionPath}:ProviderTemplateName"];
        var languageCode = configuration[$"{SandboxSectionPath}:LanguageCode"];
        var checkInDateValue = configuration[$"{SandboxSectionPath}:CheckInDateValue"] ?? "2026-08-20";
        var accessToken = configuration[$"ExternalIntegrations:WhatsApp:Secrets:{AccessTokenSecretReference}"];

        if (string.IsNullOrWhiteSpace(phoneNumberId) || string.IsNullOrWhiteSpace(recipient) ||
            string.IsNullOrWhiteSpace(providerTemplateName) || string.IsNullOrWhiteSpace(languageCode) ||
            string.IsNullOrWhiteSpace(accessToken))
        {
            _output.WriteLine(
                "SANDBOX SKIPPED — local Meta credentials are not configured. This is expected and does not " +
                "block publication (CP2.2 mandate §56). To run this proof for real, set (never paste real " +
                "values in chat/commits):\n\n" +
                $"  dotnet user-secrets set \"{SandboxSectionPath}:PhoneNumberId\" \"<meta-test-phone-number-id>\" --project src/Host/IHostPro.Api\n" +
                $"  dotnet user-secrets set \"{SandboxSectionPath}:RecipientPhoneNumber\" \"<authorized-test-recipient>\" --project src/Host/IHostPro.Api\n" +
                $"  dotnet user-secrets set \"{SandboxSectionPath}:ProviderTemplateName\" \"<meta-approved-utility-template-name>\" --project src/Host/IHostPro.Api\n" +
                $"  dotnet user-secrets set \"{SandboxSectionPath}:LanguageCode\" \"<e.g. pt_BR>\" --project src/Host/IHostPro.Api\n" +
                $"  dotnet user-secrets set \"ExternalIntegrations:WhatsApp:Secrets:{AccessTokenSecretReference}\" \"<meta-system-user-access-token>\" --project src/Host/IHostPro.Api\n\n" +
                "(equivalently, the same five keys may be set as environment variables with '__' separators, " +
                "e.g. ExternalIntegrations__WhatsApp__SandboxTest__PhoneNumberId).");
            return;
        }

        var tenantId = Guid.NewGuid();
        var integration = WhatsAppIntegration.Create(Guid.NewGuid(), tenantId, DateTimeOffset.UtcNow);
        integration.UpdateConfiguration(wabaId: null, phoneNumberId, AccessTokenSecretReference, null, null, DateTimeOffset.UtcNow);
        var mapping = WhatsAppTemplateMapping.Create(
            Guid.NewGuid(), tenantId, "RESERVATION_CONFIRMATION", providerTemplateName, languageCode, ["CheckInDate"], DateTimeOffset.UtcNow);

        var services = new ServiceCollection();
        services.AddHttpClient(MetaWhatsAppMessagingProvider.HttpClientName, client =>
        {
            client.BaseAddress = new Uri("https://graph.facebook.com/");
            client.Timeout = TimeSpan.FromSeconds(15);
        });
        await using var serviceProvider = services.BuildServiceProvider();

        var provider = new MetaWhatsAppMessagingProvider(
            serviceProvider.GetRequiredService<IHttpClientFactory>(),
            StaticIntegrationRepository(integration),
            StaticTemplateMappingRepository(mapping),
            new DevelopmentWhatsAppCredentialProvider(configuration),
            Options.Create(new MetaWhatsAppOptions()),
            NullLogger<MetaWhatsAppMessagingProvider>.Instance);

        var result = await provider.SendAsync(
            new OutboundMessageRequest(
                tenantId, Guid.NewGuid(), "WhatsApp", recipient, "RESERVATION_CONFIRMATION",
                new Dictionary<string, string> { ["CheckInDate"] = checkInDateValue }, Guid.NewGuid().ToString("D")),
            CancellationToken.None);

        _output.WriteLine($"Sandbox result: Accepted={result.Accepted}, FailureCode={result.FailureCode}, FailureCategory={result.FailureCategory}");
        // ProviderMessageId is intentionally never written to test output —
        // it is not a secret, but there is no operational need to log it here.

        result.Accepted.Should().BeTrue(
            $"a real Meta sandbox call was attempted with locally-configured credentials and should be accepted — got FailureCode={result.FailureCode}, FailureCategory={result.FailureCategory}");
        result.ProviderMessageId.Should().NotBeNullOrWhiteSpace();
    }

    private static IWhatsAppIntegrationRepository StaticIntegrationRepository(WhatsAppIntegration integration) =>
        new FixedWhatsAppIntegrationRepository(integration);

    private static IWhatsAppTemplateMappingRepository StaticTemplateMappingRepository(WhatsAppTemplateMapping mapping) =>
        new FixedWhatsAppTemplateMappingRepository(mapping);

    private sealed class FixedWhatsAppIntegrationRepository(WhatsAppIntegration integration) : IWhatsAppIntegrationRepository
    {
        public Task<WhatsAppIntegration?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<WhatsAppIntegration?>(integration.Id == id ? integration : null);

        public Task<WhatsAppIntegration?> GetForCurrentTenantAsync(CancellationToken cancellationToken) =>
            Task.FromResult<WhatsAppIntegration?>(integration);

        public void Add(WhatsAppIntegration aggregate) => throw new NotSupportedException();
        public void Update(WhatsAppIntegration aggregate) => throw new NotSupportedException();
        public void Remove(WhatsAppIntegration aggregate) => throw new NotSupportedException();
    }

    private sealed class FixedWhatsAppTemplateMappingRepository(WhatsAppTemplateMapping mapping) : IWhatsAppTemplateMappingRepository
    {
        public Task<WhatsAppTemplateMapping?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<WhatsAppTemplateMapping?>(mapping.Id == id ? mapping : null);

        public Task<WhatsAppTemplateMapping?> GetForCurrentTenantByTemplateKeyAsync(string templateKey, CancellationToken cancellationToken) =>
            Task.FromResult<WhatsAppTemplateMapping?>(mapping.TemplateKey == templateKey ? mapping : null);

        public void Add(WhatsAppTemplateMapping aggregate) => throw new NotSupportedException();
        public void Update(WhatsAppTemplateMapping aggregate) => throw new NotSupportedException();
        public void Remove(WhatsAppTemplateMapping aggregate) => throw new NotSupportedException();
    }
}
