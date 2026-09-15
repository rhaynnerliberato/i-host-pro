using FluentAssertions;
using IHostPro.Contexts.ExternalIntegrations.Infrastructure.AirbnbEmailBridge;

namespace IHostPro.Contexts.ExternalIntegrations.Tests.Unit.Infrastructure.AirbnbEmailBridge;

/// <summary>
/// Locks in the public-client, no-secret shape approved for this gate (Fase
/// 9 review §1-2, §6-8): no <c>ClientSecret</c> property exists on this class
/// at all — a compile-time guarantee, not just a runtime default.
/// </summary>
public class AirbnbEmailBridgeOptionsTests
{
    [Fact]
    public void Defaults_target_a_public_client_supporting_personal_Microsoft_accounts()
    {
        var options = new AirbnbEmailBridgeOptions();

        options.RedirectUri.Should().Be("http://localhost", "must match the Entra app registration's Mobile/Desktop platform redirect URI exactly");
        options.Authority.Should().Be("https://login.microsoftonline.com/common", "must support personal Microsoft accounts (Outlook.com/Hotmail), not just work/school accounts");
        options.Scopes.Should().BeEquivalentTo(["Mail.Read"], "least privilege - never Mail.ReadWrite/Mail.Send by default");
    }

    [Fact]
    public void ClientId_is_unset_by_default_and_that_is_a_legitimate_state()
    {
        var options = new AirbnbEmailBridgeOptions();

        options.ClientId.Should().BeNull("no developer has connected a mailbox yet by default - this must never block host startup");
    }
}
