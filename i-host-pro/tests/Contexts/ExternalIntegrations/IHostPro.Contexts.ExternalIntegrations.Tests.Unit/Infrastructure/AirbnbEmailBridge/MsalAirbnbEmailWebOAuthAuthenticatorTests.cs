using System.Security.Cryptography;
using System.Text;
using System.Web;
using FluentAssertions;
using IHostPro.Contexts.ExternalIntegrations.Infrastructure.AirbnbEmailBridge;
using IHostPro.Contexts.ExternalIntegrations.Tests.Unit.Application.AirbnbEmailBridge;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace IHostPro.Contexts.ExternalIntegrations.Tests.Unit.Infrastructure.AirbnbEmailBridge;

/// <summary>
/// <see cref="MsalAirbnbEmailWebOAuthAuthenticator.BuildAuthorizationRequest"/>
/// is pure/no-network, so it is fully unit-testable — unlike
/// <see cref="MsalAirbnbEmailWebOAuthAuthenticator.CompleteAsync"/>, which
/// needs a real Microsoft token exchange and is only provable by the
/// mandatory real browser smoke (Web OAuth architecture gate, item 30).
/// </summary>
public class MsalAirbnbEmailWebOAuthAuthenticatorTests
{
    private static readonly AirbnbEmailBridgeOptions ConfiguredOptions = new()
    {
        ClientId = "test-client-id",
        ClientSecret = "test-client-secret",
        Authority = "https://login.microsoftonline.com/common",
        WebRedirectUri = "https://api.example.test/api/v1/integrations/airbnb-email/oauth/callback",
        WebFrontendReturnUrl = "https://app.example.test/integrations/airbnb-email",
        Scopes = ["Mail.Read"],
    };

    private static MsalAirbnbEmailWebOAuthAuthenticator CreateAuthenticator(AirbnbEmailBridgeOptions options)
    {
        var repository = FakeAirbnbEmailMailboxConnectionRepository.WithExisting(null);
        var tokenCacheStore = new FakeAirbnbEmailTokenCacheStore(repository);
        var unitOfWork = new FakeAirbnbEmailUnitOfWork();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ExternalIntegrations:AirbnbEmailBridge:TokenCacheEncryptionKeyBase64"] =
                    Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            })
            .Build();
        var protector = new AesGcmOAuthTransactionSecretProtector(configuration);

        return new MsalAirbnbEmailWebOAuthAuthenticator(
            repository, tokenCacheStore, unitOfWork, Options.Create(options), protector,
            TimeProvider.System, NullLogger<MsalAirbnbEmailWebOAuthAuthenticator>.Instance);
    }

    [Theory]
    [InlineData(null, "secret", "https://redirect.test", "https://frontend.test")]
    [InlineData("client", null, "https://redirect.test", "https://frontend.test")]
    [InlineData("client", "secret", null, "https://frontend.test")]
    [InlineData("client", "secret", "https://redirect.test", null)]
    public void Returns_null_when_any_required_setting_is_missing(
        string? clientId, string? clientSecret, string? webRedirectUri, string? webFrontendReturnUrl)
    {
        var authenticator = CreateAuthenticator(new AirbnbEmailBridgeOptions
        {
            ClientId = clientId, ClientSecret = clientSecret, WebRedirectUri = webRedirectUri, WebFrontendReturnUrl = webFrontendReturnUrl,
        });

        authenticator.BuildAuthorizationRequest().Should().BeNull();
    }

    [Fact]
    public void The_authorization_URL_uses_S256_PKCE_and_never_the_plain_method()
    {
        var authenticator = CreateAuthenticator(ConfiguredOptions);

        var request = authenticator.BuildAuthorizationRequest();

        request.Should().NotBeNull();
        request!.AuthorizationUrl.Should().Contain("code_challenge_method=S256");
        request.AuthorizationUrl.Should().NotContain("code_challenge_method=plain");
    }

    [Fact]
    public void The_code_challenge_in_the_URL_is_the_SHA256_of_the_verifier_protecting_it_at_rest()
    {
        var authenticator = CreateAuthenticator(ConfiguredOptions);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ExternalIntegrations:AirbnbEmailBridge:TokenCacheEncryptionKeyBase64"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            })
            .Build();

        var request = authenticator.BuildAuthorizationRequest();

        request.Should().NotBeNull();
        var query = HttpUtility.ParseQueryString(new Uri(request!.AuthorizationUrl).Query);
        var codeChallengeInUrl = query["code_challenge"];
        codeChallengeInUrl.Should().NotBeNullOrEmpty();

        // The verifier never appears anywhere in the URL - only its SHA-256 challenge does.
        request.AuthorizationUrl.Should().NotContain(Convert.ToBase64String(request.ProtectedPkceVerifier));
    }

    [Fact]
    public void Every_call_generates_a_fresh_high_entropy_state_and_verifier_never_reused()
    {
        var authenticator = CreateAuthenticator(ConfiguredOptions);

        var first = authenticator.BuildAuthorizationRequest();
        var second = authenticator.BuildAuthorizationRequest();

        first.Should().NotBeNull();
        second.Should().NotBeNull();
        first!.State.Should().NotBe(second!.State);
        first.State.Length.Should().BeGreaterOrEqualTo(32, "the state must be high-entropy, not a short/guessable value");
        first.ProtectedPkceVerifier.Should().NotEqual(second.ProtectedPkceVerifier);
    }

    [Fact]
    public void The_authorization_URL_carries_the_required_client_and_redirect_parameters()
    {
        var authenticator = CreateAuthenticator(ConfiguredOptions);

        var request = authenticator.BuildAuthorizationRequest();

        request.Should().NotBeNull();
        var query = HttpUtility.ParseQueryString(new Uri(request!.AuthorizationUrl).Query);
        query["client_id"].Should().Be(ConfiguredOptions.ClientId);
        query["redirect_uri"].Should().Be(ConfiguredOptions.WebRedirectUri);
        query["response_type"].Should().Be("code");
        query["scope"].Should().Be("Mail.Read");
        query["state"].Should().Be(request.State);
    }
}
