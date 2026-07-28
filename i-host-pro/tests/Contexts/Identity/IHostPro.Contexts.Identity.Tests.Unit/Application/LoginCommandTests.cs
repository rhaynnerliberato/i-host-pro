using FluentAssertions;
using IHostPro.BuildingBlocks.Application;
using IHostPro.Contexts.Identity.Application;

namespace IHostPro.Contexts.Identity.Tests.Unit.Application;

public class LoginCommandTests
{
    private static LoginCommand Create(string password = "correct horse battery staple") => new(
        "acme", "user@acme.com", password, new AuthenticationRequestContext("203.0.113.7", "iPhone", "Safari"));

    [Fact]
    public void Implements_IBootstrapRequest()
    {
        (Create() is IBootstrapRequest).Should().BeTrue();
    }

    [Fact]
    public void Implements_ICommand_of_AuthTokensResult()
    {
        (Create() is ICommand<AuthTokensResult>).Should().BeTrue();
    }

    [Fact]
    public void ToString_never_includes_the_password()
    {
        const string password = "correct horse battery staple";

        var text = Create(password).ToString();

        text.Should().NotContain(password);
        text.Should().Contain("[REDACTED]");
    }

    [Fact]
    public void ToString_still_includes_the_tenant_slug_for_diagnostics()
    {
        var command = Create();

        var text = command.ToString();

        text.Should().Contain(command.TenantSlug);
    }

    [Fact]
    public void ToString_never_includes_the_email_and_carries_no_derived_identifier_either()
    {
        var command = new LoginCommand(
            "acme", "someone.identifiable@acme.com", "irrelevant", new AuthenticationRequestContext(null, null, null));

        var text = command.ToString();

        // Not just the raw address — no hash/digest of it either: e-mails
        // are low-entropy and a plain/truncated hash is feasibly reversible
        // by dictionary or enumeration attack (Incremento 2 plan, Etapa 10
        // correction).
        text.Should().NotContain("someone.identifiable@acme.com");
        text.Should().Contain("Email = [REDACTED]");
    }
}
