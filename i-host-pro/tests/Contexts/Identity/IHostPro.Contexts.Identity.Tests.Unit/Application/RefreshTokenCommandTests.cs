using FluentAssertions;
using IHostPro.BuildingBlocks.Application;
using IHostPro.Contexts.Identity.Application;

namespace IHostPro.Contexts.Identity.Tests.Unit.Application;

public class RefreshTokenCommandTests
{
    private const string SampleToken =
        "0123456789abcdef0123456789abcdef.fedcba9876543210fedcba9876543210.some-secret-value";

    private static RefreshTokenCommand Create(string token = SampleToken) => new(
        token, new AuthenticationRequestContext("203.0.113.7", "iPhone", "Safari"));

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
    public void ToString_never_includes_the_refresh_token()
    {
        var text = Create().ToString();

        text.Should().NotContain(SampleToken);
        text.Should().Contain("[REDACTED]");
    }
}
