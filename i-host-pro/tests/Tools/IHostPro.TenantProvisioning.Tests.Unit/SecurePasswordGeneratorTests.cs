using FluentAssertions;
using IHostPro.TenantProvisioning;
using Xunit;

namespace IHostPro.TenantProvisioning.Tests.Unit;

/// <summary>
/// Proves the generated password deterministically satisfies the real
/// PasswordPolicyOptions defaults (min length 10, digit+upper+lower+special)
/// without ever needing a live PasswordPolicyValidator instance - a pure,
/// no-I/O guarantee this class exists specifically to make.
/// </summary>
public class SecurePasswordGeneratorTests
{
    [Fact]
    public void Generated_password_meets_the_default_length()
    {
        SecurePasswordGenerator.Generate().Should().HaveLength(32);
    }

    [Fact]
    public void Generated_password_contains_at_least_one_character_from_every_required_category()
    {
        for (var i = 0; i < 200; i++)
        {
            var password = SecurePasswordGenerator.Generate();

            password.Any(char.IsUpper).Should().BeTrue();
            password.Any(char.IsLower).Should().BeTrue();
            password.Any(char.IsDigit).Should().BeTrue();
            password.Any(c => !char.IsLetterOrDigit(c)).Should().BeTrue();
        }
    }

    [Fact]
    public void Two_generated_passwords_are_never_equal()
    {
        var first = SecurePasswordGenerator.Generate();
        var second = SecurePasswordGenerator.Generate();

        first.Should().NotBe(second);
    }

    [Fact]
    public void Rejects_a_length_below_the_password_policy_minimum()
    {
        var act = () => SecurePasswordGenerator.Generate(9);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
