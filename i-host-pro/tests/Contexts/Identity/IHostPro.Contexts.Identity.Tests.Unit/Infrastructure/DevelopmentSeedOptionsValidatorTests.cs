using FluentAssertions;
using IHostPro.Contexts.Identity.Infrastructure.Seed;

namespace IHostPro.Contexts.Identity.Tests.Unit.Infrastructure;

public class DevelopmentSeedOptionsValidatorTests
{
    private static readonly DevelopmentSeedOptionsValidator Validator = new();

    private static DevelopmentSeedOptions Enabled() => new()
    {
        Enabled = true,
        TenantSlug = "dev-tenant",
        TenantName = "Dev Tenant",
        AdminEmail = "admin@dev.local",
        AdminFullName = "Development Admin",
        AdminPassword = "supplied-via-user-secrets-or-env-var",
    };

    [Fact]
    public void Validate_succeeds_when_disabled_regardless_of_other_fields_being_empty()
    {
        var options = new DevelopmentSeedOptions { Enabled = false };

        Validator.Validate(name: null, options).Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validate_succeeds_when_enabled_and_every_field_is_present()
    {
        Validator.Validate(name: null, Enabled()).Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validate_fails_when_enabled_and_the_password_is_missing()
    {
        var options = Enabled();
        options.AdminPassword = string.Empty;

        var result = Validator.Validate(name: null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain(nameof(DevelopmentSeedOptions.AdminPassword));
        result.FailureMessage.Should().Contain("environment variable");
    }

    [Fact]
    public void Validate_failure_message_never_contains_the_configured_password_value()
    {
        var options = Enabled();
        options.AdminPassword = string.Empty;
        const string secretThatMustNeverLeak = "supplied-via-user-secrets-or-env-var";

        var result = Validator.Validate(name: null, options);

        result.FailureMessage.Should().NotContain(secretThatMustNeverLeak);
    }

    [Fact]
    public void Validate_fails_when_enabled_and_the_tenant_slug_is_missing()
    {
        var options = Enabled();
        options.TenantSlug = string.Empty;

        var result = Validator.Validate(name: null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain(nameof(DevelopmentSeedOptions.TenantSlug));
    }

    [Theory]
    [InlineData("ab")] // 2 chars — below the 3-char minimum
    public void Validate_fails_when_enabled_and_the_tenant_slug_is_too_short(string slug)
    {
        var options = Enabled();
        options.TenantSlug = slug;

        var result = Validator.Validate(name: null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain(nameof(DevelopmentSeedOptions.TenantSlug));
    }

    [Fact]
    public void Validate_fails_when_enabled_and_the_tenant_name_is_missing()
    {
        var options = Enabled();
        options.TenantName = string.Empty;

        var result = Validator.Validate(name: null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain(nameof(DevelopmentSeedOptions.TenantName));
    }

    [Fact]
    public void Validate_fails_when_enabled_and_the_admin_full_name_is_missing()
    {
        var options = Enabled();
        options.AdminFullName = string.Empty;

        var result = Validator.Validate(name: null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain(nameof(DevelopmentSeedOptions.AdminFullName));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-an-email")]
    public void Validate_fails_when_enabled_and_the_admin_email_is_missing_or_malformed(string email)
    {
        var options = Enabled();
        options.AdminEmail = email;

        var result = Validator.Validate(name: null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain(nameof(DevelopmentSeedOptions.AdminEmail));
    }

    [Fact]
    public void Validate_accumulates_every_failure_when_enabled_with_nothing_configured()
    {
        var options = new DevelopmentSeedOptions { Enabled = true };

        var result = Validator.Validate(name: null, options);

        // TenantSlug, TenantName, AdminEmail, AdminFullName, AdminPassword.
        result.Failures.Should().HaveCount(5);
    }
}
