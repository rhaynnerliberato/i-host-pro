using FluentAssertions;
using IHostPro.Contexts.Identity.Application;

namespace IHostPro.Contexts.Identity.Tests.Unit.Application;

public class RefreshTokenCommandValidatorTests
{
    private static readonly RefreshTokenCommandValidator Validator = new();
    private static readonly AuthenticationRequestContext Context = new("203.0.113.7", "iPhone", "Safari");

    private const string SampleToken =
        "0123456789abcdef0123456789abcdef.fedcba9876543210fedcba9876543210.some-secret-value";

    private static RefreshTokenCommand Valid() => new(SampleToken, Context);

    [Fact]
    public void Validate_succeeds_for_a_well_formed_command()
    {
        Validator.Validate(Valid()).IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void Validate_fails_for_a_missing_refresh_token(string? token)
    {
        var command = Valid() with { RefreshToken = token! };

        var result = Validator.Validate(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorCode == "refresh_token_required");
    }

    [Fact]
    public void Validate_fails_for_a_refresh_token_exceeding_the_shared_defensive_maximum()
    {
        var command = Valid() with { RefreshToken = new string('a', RefreshTokenLimits.MaxTotalLength + 1) };

        var result = Validator.Validate(command);

        result.Errors.Should().Contain(e => e.ErrorCode == "refresh_token_too_long");
    }

    [Fact]
    public void Validate_accepts_a_refresh_token_at_exactly_the_maximum_length()
    {
        var command = Valid() with { RefreshToken = new string('a', RefreshTokenLimits.MaxTotalLength) };

        Validator.Validate(command).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_fails_for_a_missing_request_context()
    {
        var command = Valid() with { RequestContext = null! };

        var result = Validator.Validate(command);

        result.Errors.Should().Contain(e => e.ErrorCode == "request_context_required");
    }

    [Fact]
    public void Validate_accumulates_every_failure_instead_of_stopping_at_the_first()
    {
        var command = new RefreshTokenCommand(string.Empty, null!);

        var result = Validator.Validate(command);

        result.Errors.Select(e => e.ErrorCode).Should().Contain(["refresh_token_required", "request_context_required"]);
    }

    [Fact]
    public void No_validation_error_message_ever_contains_the_submitted_refresh_token()
    {
        var oversizedToken = SampleToken + new string('a', RefreshTokenLimits.MaxTotalLength);
        var command = Valid() with { RefreshToken = oversizedToken };

        var result = Validator.Validate(command);

        result.Errors.Should().NotBeEmpty();
        result.Errors.Should().OnlyContain(e => !e.ErrorMessage.Contains(oversizedToken));
    }
}
