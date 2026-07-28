using FluentAssertions;
using IHostPro.Contexts.Identity.Infrastructure.Security;

namespace IHostPro.Contexts.Identity.Tests.Unit.Infrastructure;

public class AccountLockoutOptionsValidatorTests
{
    private static readonly AccountLockoutOptionsValidator Validator = new();

    private static AccountLockoutOptions Valid() => new()
    {
        MaxFailedAccessAttempts = 5,
        DefaultLockoutDuration = TimeSpan.FromMinutes(5),
        AllowedForNewUsers = true,
    };

    [Fact]
    public void Validate_accepts_the_default_values()
    {
        Validator.Validate(name: null, Valid()).Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(21)]
    public void Validate_rejects_a_max_failed_access_attempts_outside_the_allowed_bounds(int attempts)
    {
        var options = Valid();
        options.MaxFailedAccessAttempts = attempts;

        var result = Validator.Validate(name: null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain(nameof(AccountLockoutOptions.MaxFailedAccessAttempts));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(20)]
    public void Validate_accepts_a_max_failed_access_attempts_at_the_boundaries(int attempts)
    {
        var options = Valid();
        options.MaxFailedAccessAttempts = attempts;

        Validator.Validate(name: null, options).Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validate_rejects_a_lockout_duration_below_the_minimum()
    {
        var options = Valid();
        options.DefaultLockoutDuration = TimeSpan.FromSeconds(30);

        var result = Validator.Validate(name: null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain(nameof(AccountLockoutOptions.DefaultLockoutDuration));
    }

    [Fact]
    public void Validate_rejects_a_lockout_duration_above_the_maximum()
    {
        var options = Valid();
        options.DefaultLockoutDuration = TimeSpan.FromHours(25);

        var result = Validator.Validate(name: null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain(nameof(AccountLockoutOptions.DefaultLockoutDuration));
    }

    [Fact]
    public void Validate_accumulates_every_failure_instead_of_stopping_at_the_first()
    {
        var options = new AccountLockoutOptions { MaxFailedAccessAttempts = 0, DefaultLockoutDuration = TimeSpan.Zero };

        var result = Validator.Validate(name: null, options);

        result.Failures.Should().HaveCount(2);
    }
}
