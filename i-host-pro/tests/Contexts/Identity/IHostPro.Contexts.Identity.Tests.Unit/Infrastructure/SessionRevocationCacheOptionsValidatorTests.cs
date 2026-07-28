using FluentAssertions;
using IHostPro.Contexts.Identity.Infrastructure.Caching;

namespace IHostPro.Contexts.Identity.Tests.Unit.Infrastructure;

public class SessionRevocationCacheOptionsValidatorTests
{
    private static readonly SessionRevocationCacheOptionsValidator Validator = new();

    private static SessionRevocationCacheOptions Valid() => new()
    {
        ConnectionString = "localhost:6379",
        ConnectTimeout = TimeSpan.FromSeconds(1),
        OperationTimeout = TimeSpan.FromSeconds(1),
        ConnectRetry = 1,
    };

    [Fact]
    public void Validate_succeeds_for_the_documented_defaults()
    {
        Validator.Validate(name: null, Valid()).Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validate_fails_when_ConnectionString_is_missing()
    {
        var options = Valid();
        options.ConnectionString = string.Empty;

        var result = Validator.Validate(name: null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain(nameof(SessionRevocationCacheOptions.ConnectionString));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(50)]
    public void Validate_fails_when_ConnectTimeout_is_below_the_minimum(int milliseconds)
    {
        var options = Valid();
        options.ConnectTimeout = TimeSpan.FromMilliseconds(milliseconds);

        var result = Validator.Validate(name: null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain(nameof(SessionRevocationCacheOptions.ConnectTimeout));
    }

    [Fact]
    public void Validate_fails_when_ConnectTimeout_exceeds_the_maximum()
    {
        var options = Valid();
        options.ConnectTimeout = TimeSpan.FromSeconds(11);

        var result = Validator.Validate(name: null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain(nameof(SessionRevocationCacheOptions.ConnectTimeout));
    }

    [Fact]
    public void Validate_fails_when_OperationTimeout_is_below_the_minimum()
    {
        var options = Valid();
        options.OperationTimeout = TimeSpan.FromMilliseconds(100);

        var result = Validator.Validate(name: null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain(nameof(SessionRevocationCacheOptions.OperationTimeout));
    }

    [Fact]
    public void Validate_fails_when_ConnectRetry_is_negative()
    {
        var options = Valid();
        options.ConnectRetry = -1;

        var result = Validator.Validate(name: null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain(nameof(SessionRevocationCacheOptions.ConnectRetry));
    }

    [Fact]
    public void Validate_fails_when_ConnectRetry_exceeds_the_maximum()
    {
        var options = Valid();
        options.ConnectRetry = 6;

        var result = Validator.Validate(name: null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain(nameof(SessionRevocationCacheOptions.ConnectRetry));
    }

    [Fact]
    public void Validate_accumulates_every_failure_at_once()
    {
        var options = new SessionRevocationCacheOptions
        {
            ConnectionString = string.Empty,
            ConnectTimeout = TimeSpan.Zero,
            OperationTimeout = TimeSpan.Zero,
            ConnectRetry = -1,
        };

        var result = Validator.Validate(name: null, options);

        result.Failures.Should().HaveCount(4);
    }
}
