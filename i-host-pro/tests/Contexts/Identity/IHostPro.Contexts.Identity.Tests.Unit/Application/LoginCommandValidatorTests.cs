using FluentAssertions;
using IHostPro.Contexts.Identity.Application;

namespace IHostPro.Contexts.Identity.Tests.Unit.Application;

public class LoginCommandValidatorTests
{
    private static readonly LoginCommandValidator Validator = new();
    private static readonly AuthenticationRequestContext Context = new("203.0.113.7", "iPhone", "Safari");

    private static LoginCommand Valid() => new("acme-hospitality", "user@acme.com", "a-reasonable-password", Context);

    [Fact]
    public void Validate_succeeds_for_a_well_formed_command()
    {
        Validator.Validate(Valid()).IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void Validate_fails_for_a_missing_tenant_slug(string? slug)
    {
        var command = Valid() with { TenantSlug = slug! };

        var result = Validator.Validate(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorCode == "tenant_slug_required");
    }

    [Fact]
    public void Validate_fails_for_a_tenant_slug_exceeding_the_maximum_length()
    {
        var command = Valid() with { TenantSlug = new string('a', 64) };

        var result = Validator.Validate(command);

        result.Errors.Should().Contain(e => e.ErrorCode == "tenant_slug_too_long");
    }

    [Theory]
    [InlineData("AB")] // too short for TenantSlug's own format rule
    [InlineData("has_underscore")] // '_' is outside TenantSlug's [a-z0-9-] class — lowercasing does not fix it
    [InlineData("has spaces")]
    public void Validate_fails_for_a_malformed_tenant_slug(string slug)
    {
        var command = Valid() with { TenantSlug = slug };

        var result = Validator.Validate(command);

        result.Errors.Should().Contain(e => e.ErrorCode == "tenant_slug_invalid_format");
    }

    [Fact]
    public void Validate_accepts_an_uppercase_tenant_slug_because_TenantSlug_normalizes_case_itself()
    {
        // Not a bug: TenantSlug.Create (Identity.Domain, Incremento 1) trims
        // and lowercases before validating format — this validator reuses
        // that same logic (DRY) rather than duplicating a stricter,
        // divergent rule of its own.
        var command = Valid() with { TenantSlug = "ACME-Hospitality" };

        Validator.Validate(command).IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void Validate_fails_for_a_missing_email(string? email)
    {
        var command = Valid() with { Email = email! };

        var result = Validator.Validate(command);

        result.Errors.Should().Contain(e => e.ErrorCode == "email_required");
    }

    [Fact]
    public void Validate_fails_for_an_email_exceeding_the_maximum_length()
    {
        var command = Valid() with { Email = new string('a', 315) + "@a.com" };

        var result = Validator.Validate(command);

        result.Errors.Should().Contain(e => e.ErrorCode == "email_too_long");
    }

    [Theory]
    [InlineData("not-an-email")]
    [InlineData("missing-at-sign.com")]
    [InlineData("has spaces@example.com")]
    public void Validate_fails_for_a_malformed_email(string email)
    {
        var command = Valid() with { Email = email };

        var result = Validator.Validate(command);

        result.Errors.Should().Contain(e => e.ErrorCode == "email_invalid_format");
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void Validate_fails_for_a_missing_password(string? password)
    {
        var command = Valid() with { Password = password! };

        var result = Validator.Validate(command);

        result.Errors.Should().Contain(e => e.ErrorCode == "password_required");
    }

    [Fact]
    public void Validate_fails_for_a_password_exceeding_the_defensive_maximum()
    {
        var command = Valid() with { Password = new string('a', LoginCommandValidator.MaxPasswordLength + 1) };

        var result = Validator.Validate(command);

        result.Errors.Should().Contain(e => e.ErrorCode == "password_too_long");
    }

    [Fact]
    public void Validate_accepts_a_password_at_exactly_the_maximum_length()
    {
        var command = Valid() with { Password = new string('a', LoginCommandValidator.MaxPasswordLength) };

        Validator.Validate(command).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_does_not_enforce_any_minimum_password_length_or_complexity()
    {
        // Deliberate: login must accept a correct password for an account
        // created under an older, looser policy — minimum length/complexity
        // belongs to password *creation*, never to authentication.
        var command = Valid() with { Password = "x" };

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
        var command = new LoginCommand(string.Empty, string.Empty, string.Empty, null!);

        var result = Validator.Validate(command);

        result.Errors.Select(e => e.ErrorCode).Should().Contain(
            ["tenant_slug_required", "email_required", "password_required", "request_context_required"]);
    }

    [Fact]
    public void No_validation_error_message_ever_contains_the_submitted_password()
    {
        const string password = "super-secret-password-value";
        var command = Valid() with { Password = password + new string('a', LoginCommandValidator.MaxPasswordLength) };

        var result = Validator.Validate(command);

        result.Errors.Should().NotBeEmpty();
        result.Errors.Should().OnlyContain(e => !e.ErrorMessage.Contains(password));
    }
}
