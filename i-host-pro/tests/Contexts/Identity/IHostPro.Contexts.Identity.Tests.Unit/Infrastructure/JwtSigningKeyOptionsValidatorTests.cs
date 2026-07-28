using System.Security.Cryptography;
using FluentAssertions;
using IHostPro.Contexts.Identity.Infrastructure.Security;

namespace IHostPro.Contexts.Identity.Tests.Unit.Infrastructure;

public class JwtSigningKeyOptionsValidatorTests
{
    private static readonly JwtSigningKeyOptionsValidator Validator = new();

    private static string GeneratePem(int keySizeBits)
    {
        using var rsa = RSA.Create(keySizeBits);
        return rsa.ExportRSAPrivateKeyPem();
    }

    [Fact]
    public void Validate_succeeds_for_a_valid_2048_bit_key()
    {
        var options = new JwtSigningKeyOptions { PrivateKeyPem = GeneratePem(2048) };

        Validator.Validate(name: null, options).Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validate_fails_for_a_missing_key()
    {
        var options = new JwtSigningKeyOptions { PrivateKeyPem = string.Empty };

        var result = Validator.Validate(name: null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain(nameof(JwtSigningKeyOptions.PrivateKeyPem));
    }

    [Fact]
    public void Validate_fails_for_a_key_smaller_than_2048_bits()
    {
        var options = new JwtSigningKeyOptions { PrivateKeyPem = GeneratePem(1024) };

        var result = Validator.Validate(name: null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("2048");
    }

    [Fact]
    public void Validate_failure_message_never_contains_the_configured_key_material()
    {
        var pem = GeneratePem(1024);
        var options = new JwtSigningKeyOptions { PrivateKeyPem = pem };

        var result = Validator.Validate(name: null, options);

        result.FailureMessage.Should().NotContain("BEGIN RSA PRIVATE KEY");
    }
}
