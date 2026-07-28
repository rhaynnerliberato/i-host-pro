using FluentAssertions;
using IHostPro.Contexts.Identity.Infrastructure.Security;

namespace IHostPro.Contexts.Identity.Tests.Unit.Infrastructure;

public class JwtOptionsValidatorTests
{
    private static readonly JwtOptionsValidator Validator = new();

    private static JwtOptions Valid() => new()
    {
        Issuer = "https://identity.ihostpro.local",
        Audience = "ihostpro-api",
        AccessTokenLifetime = TimeSpan.FromMinutes(15),
        ClockSkew = TimeSpan.FromSeconds(60),
    };

    [Fact]
    public void Validate_accepts_the_default_values_with_issuer_and_audience_set()
    {
        var result = Validator.Validate(name: null, Valid());

        result.Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_rejects_a_missing_issuer(string? issuer)
    {
        var options = Valid();
        options.Issuer = issuer!;

        var result = Validator.Validate(name: null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain(nameof(JwtOptions.Issuer));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Validate_rejects_a_missing_audience(string? audience)
    {
        var options = Valid();
        options.Audience = audience!;

        var result = Validator.Validate(name: null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain(nameof(JwtOptions.Audience));
    }

    [Fact]
    public void Validate_rejects_an_access_token_lifetime_below_the_minimum()
    {
        var options = Valid();
        options.AccessTokenLifetime = TimeSpan.FromSeconds(59);

        var result = Validator.Validate(name: null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain(nameof(JwtOptions.AccessTokenLifetime));
    }

    [Fact]
    public void Validate_accepts_an_access_token_lifetime_at_the_minimum_boundary()
    {
        var options = Valid();
        options.AccessTokenLifetime = TimeSpan.FromMinutes(1);

        Validator.Validate(name: null, options).Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validate_rejects_an_access_token_lifetime_above_the_maximum()
    {
        var options = Valid();
        options.AccessTokenLifetime = TimeSpan.FromMinutes(61);

        var result = Validator.Validate(name: null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain(nameof(JwtOptions.AccessTokenLifetime));
    }

    [Fact]
    public void Validate_accepts_an_access_token_lifetime_at_the_maximum_boundary()
    {
        var options = Valid();
        options.AccessTokenLifetime = TimeSpan.FromMinutes(60);

        Validator.Validate(name: null, options).Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validate_rejects_a_negative_clock_skew()
    {
        var options = Valid();
        options.ClockSkew = TimeSpan.FromSeconds(-1);

        var result = Validator.Validate(name: null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain(nameof(JwtOptions.ClockSkew));
    }

    [Fact]
    public void Validate_accepts_a_zero_clock_skew()
    {
        var options = Valid();
        options.ClockSkew = TimeSpan.Zero;

        Validator.Validate(name: null, options).Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validate_rejects_a_clock_skew_above_the_maximum()
    {
        var options = Valid();
        options.ClockSkew = TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1);

        var result = Validator.Validate(name: null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain(nameof(JwtOptions.ClockSkew));
    }

    [Fact]
    public void Validate_accumulates_every_failure_instead_of_stopping_at_the_first()
    {
        var options = new JwtOptions
        {
            Issuer = string.Empty,
            Audience = string.Empty,
            AccessTokenLifetime = TimeSpan.FromMinutes(61),
            ClockSkew = TimeSpan.FromMinutes(10),
        };

        var result = Validator.Validate(name: null, options);

        result.Failures.Should().HaveCount(4);
    }
}
