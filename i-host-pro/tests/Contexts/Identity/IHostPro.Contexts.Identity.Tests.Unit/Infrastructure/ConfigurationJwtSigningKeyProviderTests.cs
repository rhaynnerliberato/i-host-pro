using System.Security.Cryptography;
using FluentAssertions;
using IHostPro.Contexts.Identity.Infrastructure.Security;
using Microsoft.Extensions.Options;

namespace IHostPro.Contexts.Identity.Tests.Unit.Infrastructure;

public class ConfigurationJwtSigningKeyProviderTests
{
    private static string GeneratePem(int keySizeBits)
    {
        using var rsa = RSA.Create(keySizeBits);
        return rsa.ExportRSAPrivateKeyPem();
    }

    private static ConfigurationJwtSigningKeyProvider CreateProvider(string pem) =>
        new(Options.Create(new JwtSigningKeyOptions { PrivateKeyPem = pem }));

    [Fact]
    public void GetCurrentSigningKey_exposes_a_kid_and_RS256_capable_credentials()
    {
        using var provider = CreateProvider(GeneratePem(2048));

        var signingKey = provider.GetCurrentSigningKey();

        signingKey.KeyId.Should().NotBeNullOrWhiteSpace();
        signingKey.SigningCredentials.Algorithm.Should().Be("RS256");
    }

    [Fact]
    public void GetValidationKeys_contains_exactly_the_current_signing_keys_public_counterpart()
    {
        using var provider = CreateProvider(GeneratePem(2048));

        var signingKey = provider.GetCurrentSigningKey();
        var validationKeys = provider.GetValidationKeys();

        validationKeys.Should().ContainSingle(k => k.KeyId == signingKey.KeyId);
    }

    [Fact]
    public void The_same_key_material_always_produces_the_same_kid()
    {
        var pem = GeneratePem(2048);

        using var providerA = CreateProvider(pem);
        using var providerB = CreateProvider(pem);

        providerA.GetCurrentSigningKey().KeyId.Should().Be(providerB.GetCurrentSigningKey().KeyId);
    }

    [Fact]
    public void Different_key_material_always_produces_a_different_kid()
    {
        using var providerA = CreateProvider(GeneratePem(2048));
        using var providerB = CreateProvider(GeneratePem(2048));

        providerA.GetCurrentSigningKey().KeyId.Should().NotBe(providerB.GetCurrentSigningKey().KeyId);
    }

    [Fact]
    public void Construction_rejects_a_key_smaller_than_2048_bits()
    {
        var act = () => CreateProvider(GeneratePem(1024));

        act.Should().Throw<InvalidOperationException>().WithMessage("*2048*");
    }

    [Fact]
    public void Construction_rejects_a_missing_key()
    {
        var act = () => CreateProvider(string.Empty);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Construction_rejects_a_malformed_pem()
    {
        var act = () => CreateProvider("not a real PEM-encoded key");

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void A_2048_bit_key_is_accepted_at_exactly_the_minimum_boundary()
    {
        var act = () => CreateProvider(GeneratePem(2048));

        act.Should().NotThrow();
    }
}
