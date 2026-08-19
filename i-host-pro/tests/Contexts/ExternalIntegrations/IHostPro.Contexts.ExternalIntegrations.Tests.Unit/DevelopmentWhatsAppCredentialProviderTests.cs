using FluentAssertions;
using IHostPro.Contexts.ExternalIntegrations.Infrastructure;
using Microsoft.Extensions.Configuration;

namespace IHostPro.Contexts.ExternalIntegrations.Tests.Unit;

public class DevelopmentWhatsAppCredentialProviderTests
{
    [Fact]
    public async Task GetSecretAsync_resolves_a_configured_reference_from_configuration()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ExternalIntegrations:WhatsApp:Secrets:my-access-token-ref"] = "the-real-secret-value",
            })
            .Build();
        var provider = new DevelopmentWhatsAppCredentialProvider(configuration);

        var secret = await provider.GetSecretAsync("my-access-token-ref", CancellationToken.None);

        secret.Should().Be("the-real-secret-value");
    }

    [Fact]
    public async Task GetSecretAsync_returns_null_for_a_reference_that_was_never_configured()
    {
        var configuration = new ConfigurationBuilder().Build();
        var provider = new DevelopmentWhatsAppCredentialProvider(configuration);

        var secret = await provider.GetSecretAsync("never-configured-ref", CancellationToken.None);

        secret.Should().BeNull("a missing credential must be detectable, never silently substituted");
    }
}
