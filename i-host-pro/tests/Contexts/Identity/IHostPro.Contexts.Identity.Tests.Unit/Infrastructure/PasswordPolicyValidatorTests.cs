using FluentAssertions;
using IHostPro.Contexts.Identity.Infrastructure.Security;
using Microsoft.Extensions.Options;

namespace IHostPro.Contexts.Identity.Tests.Unit.Infrastructure;

public class PasswordPolicyValidatorTests
{
    private static PasswordPolicyValidator CreateValidator(PasswordPolicyOptions? options = null) =>
        new(Options.Create(options ?? new PasswordPolicyOptions()));

    [Theory]
    [InlineData("Sh0rt!")] // below the default minimum length of 10
    [InlineData("nouppercasehere1!")]
    [InlineData("NOLOWERCASEHERE1!")]
    [InlineData("NoDigitsHere!")]
    [InlineData("NoSpecialChars1")]
    public async Task ValidateAsync_rejects_passwords_violating_the_configured_policy(string password)
    {
        var validator = CreateValidator();

        var result = await validator.ValidateAsync(manager: null!, user: null!, password);

        result.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task ValidateAsync_accepts_a_password_satisfying_every_configured_rule()
    {
        var validator = CreateValidator();

        var result = await validator.ValidateAsync(manager: null!, user: null!, "C0rrect-Horse-Battery!");

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task ValidateAsync_respects_a_relaxed_configuration()
    {
        var relaxed = new PasswordPolicyOptions
        {
            MinimumLength = 4,
            RequireDigit = false,
            RequireUppercase = false,
            RequireLowercase = false,
            RequireSpecialCharacter = false,
        };
        var validator = CreateValidator(relaxed);

        var result = await validator.ValidateAsync(manager: null!, user: null!, "abcd");

        result.Succeeded.Should().BeTrue();
    }
}
