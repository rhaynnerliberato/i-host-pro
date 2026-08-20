using FluentAssertions;
using IHostPro.Contexts.ExternalIntegrations.Infrastructure;
using Microsoft.Extensions.Configuration;

namespace IHostPro.Contexts.ExternalIntegrations.Tests.Unit;

public class DevelopmentWhatsAppWebhookCredentialProviderTests
{
    [Fact]
    public async Task GetAppSecretAsync_resolves_the_configured_value()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ExternalIntegrations:WhatsApp:Webhook:AppSecret"] = "the-real-app-secret",
            })
            .Build();
        var provider = new DevelopmentWhatsAppWebhookCredentialProvider(configuration);

        var secret = await provider.GetAppSecretAsync(CancellationToken.None);

        secret.Should().Be("the-real-app-secret");
    }

    [Fact]
    public async Task GetVerifyTokenAsync_resolves_the_configured_value()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ExternalIntegrations:WhatsApp:Webhook:VerifyToken"] = "the-real-verify-token",
            })
            .Build();
        var provider = new DevelopmentWhatsAppWebhookCredentialProvider(configuration);

        var token = await provider.GetVerifyTokenAsync(CancellationToken.None);

        token.Should().Be("the-real-verify-token");
    }

    [Fact]
    public async Task GetAppSecretAsync_returns_null_when_never_configured()
    {
        var configuration = new ConfigurationBuilder().Build();
        var provider = new DevelopmentWhatsAppWebhookCredentialProvider(configuration);

        var secret = await provider.GetAppSecretAsync(CancellationToken.None);

        secret.Should().BeNull("a missing credential must be detectable, never silently substituted");
    }

    [Fact]
    public async Task GetVerifyTokenAsync_returns_null_when_never_configured()
    {
        var configuration = new ConfigurationBuilder().Build();
        var provider = new DevelopmentWhatsAppWebhookCredentialProvider(configuration);

        var token = await provider.GetVerifyTokenAsync(CancellationToken.None);

        token.Should().BeNull("a missing credential must be detectable, never silently substituted");
    }
}
