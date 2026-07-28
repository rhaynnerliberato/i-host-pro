using FluentAssertions;
using IHostPro.Contexts.Identity.Application;

namespace IHostPro.Contexts.Identity.Tests.Unit.Application;

public class AuthTokensResultTests
{
    private static readonly DateTimeOffset Utc = new(2026, 7, 27, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset NonUtc = new(2026, 7, 27, 9, 0, 0, TimeSpan.FromHours(-3));

    private static AuthTokensResult Create(
        DateTimeOffset? accessExpires = null, DateTimeOffset? refreshExpires = null) =>
        new("access-token-value", accessExpires ?? Utc, "refresh-token-value", refreshExpires ?? Utc);

    [Fact]
    public void TokenType_is_always_Bearer()
    {
        Create().TokenType.Should().Be("Bearer");
    }

    [Fact]
    public void Constructor_rejects_a_non_utc_access_token_expiration()
    {
        var act = () => Create(accessExpires: NonUtc);

        act.Should().Throw<ArgumentException>().WithMessage("*UTC*");
    }

    [Fact]
    public void Constructor_rejects_a_non_utc_refresh_token_expiration()
    {
        var act = () => Create(refreshExpires: NonUtc);

        act.Should().Throw<ArgumentException>().WithMessage("*UTC*");
    }

    [Fact]
    public void Constructor_rejects_an_empty_access_token()
    {
        var act = () => new AuthTokensResult(string.Empty, Utc, "refresh-token-value", Utc);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Constructor_rejects_an_empty_refresh_token()
    {
        var act = () => new AuthTokensResult("access-token-value", Utc, string.Empty, Utc);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ToString_never_includes_the_access_or_refresh_token()
    {
        var result = Create();

        var text = result.ToString();

        text.Should().NotContain("access-token-value");
        text.Should().NotContain("refresh-token-value");
        text.Should().Contain("[REDACTED]");
        text.Should().Contain("Bearer");
    }
}
