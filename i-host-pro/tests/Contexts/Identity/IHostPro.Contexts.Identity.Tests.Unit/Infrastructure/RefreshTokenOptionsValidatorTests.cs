using FluentAssertions;
using IHostPro.Contexts.Identity.Infrastructure.Security;

namespace IHostPro.Contexts.Identity.Tests.Unit.Infrastructure;

public class RefreshTokenOptionsValidatorTests
{
    private static readonly RefreshTokenOptionsValidator Validator = new();

    private static RefreshTokenOptions Valid() => new()
    {
        Lifetime = TimeSpan.FromDays(30),
        SecretSizeBytes = 32,
        ConcurrentRotationGraceWindow = TimeSpan.FromSeconds(10),
    };

    [Fact]
    public void Validate_accepts_the_default_values()
    {
        Validator.Validate(name: null, Valid()).Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData(23)] // below the 1-day minimum, expressed in hours for clarity
    public void Validate_rejects_a_lifetime_below_the_minimum(int hours)
    {
        var options = Valid();
        options.Lifetime = TimeSpan.FromHours(hours);

        var result = Validator.Validate(name: null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain(nameof(RefreshTokenOptions.Lifetime));
    }

    [Fact]
    public void Validate_accepts_a_lifetime_at_the_minimum_boundary()
    {
        var options = Valid();
        options.Lifetime = TimeSpan.FromDays(1);

        Validator.Validate(name: null, options).Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validate_rejects_a_lifetime_above_the_maximum()
    {
        var options = Valid();
        options.Lifetime = TimeSpan.FromDays(91);

        var result = Validator.Validate(name: null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain(nameof(RefreshTokenOptions.Lifetime));
    }

    [Fact]
    public void Validate_accepts_a_lifetime_at_the_maximum_boundary()
    {
        var options = Valid();
        options.Lifetime = TimeSpan.FromDays(90);

        Validator.Validate(name: null, options).Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData(15)] // below the 16-byte (128-bit) minimum
    [InlineData(65)] // above the 64-byte (512-bit) maximum
    public void Validate_rejects_a_secret_size_outside_the_allowed_bounds(int bytes)
    {
        var options = Valid();
        options.SecretSizeBytes = bytes;

        var result = Validator.Validate(name: null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain(nameof(RefreshTokenOptions.SecretSizeBytes));
    }

    [Theory]
    [InlineData(16)]
    [InlineData(64)]
    public void Validate_accepts_a_secret_size_at_the_boundaries(int bytes)
    {
        var options = Valid();
        options.SecretSizeBytes = bytes;

        Validator.Validate(name: null, options).Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validate_rejects_a_grace_window_above_the_maximum()
    {
        var options = Valid();
        options.ConcurrentRotationGraceWindow = TimeSpan.FromSeconds(61);

        var result = Validator.Validate(name: null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain(nameof(RefreshTokenOptions.ConcurrentRotationGraceWindow));
    }

    [Fact]
    public void Validate_accepts_a_zero_grace_window()
    {
        var options = Valid();
        options.ConcurrentRotationGraceWindow = TimeSpan.Zero;

        Validator.Validate(name: null, options).Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validate_accepts_a_grace_window_at_the_maximum_boundary()
    {
        var options = Valid();
        options.ConcurrentRotationGraceWindow = TimeSpan.FromSeconds(60);

        Validator.Validate(name: null, options).Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validate_accepts_the_smallest_lifetime_combined_with_the_largest_grace_window()
    {
        // The individual bounds (Lifetime >= 1 day, ConcurrentRotationGraceWindow
        // <= 60s) make it impossible for a configuration that passes both
        // individual checks to ever violate the cross-field invariant
        // (grace window < lifetime) — this test documents that boundary
        // combination explicitly, so a future change to either bound that
        // makes the cross-check reachable is forced to reconsider this test.
        var options = new RefreshTokenOptions
        {
            Lifetime = TimeSpan.FromDays(1),
            SecretSizeBytes = 32,
            ConcurrentRotationGraceWindow = TimeSpan.FromSeconds(60),
        };

        Validator.Validate(name: null, options).Succeeded.Should().BeTrue();
    }
}
