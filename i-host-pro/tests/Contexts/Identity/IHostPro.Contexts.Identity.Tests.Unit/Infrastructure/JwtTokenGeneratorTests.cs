using System.Security.Cryptography;
using FluentAssertions;
using IHostPro.Contexts.Identity.Application;
using IHostPro.Contexts.Identity.Infrastructure.Security;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace IHostPro.Contexts.Identity.Tests.Unit.Infrastructure;

public class JwtTokenGeneratorTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 7, 27, 12, 0, 0, TimeSpan.Zero);
    private static readonly JwtOptions Options = new()
    {
        Issuer = "https://identity.ihostpro.local",
        Audience = "ihostpro-api",
        AccessTokenLifetime = TimeSpan.FromMinutes(15),
        ClockSkew = TimeSpan.FromSeconds(60),
    };

    private readonly ConfigurationJwtSigningKeyProvider _keyProvider;
    private readonly JwtTokenGenerator _generator;
    private readonly JsonWebTokenHandler _handler = new();

    public JwtTokenGeneratorTests()
    {
        _keyProvider = CreateKeyProvider();
        _generator = new JwtTokenGenerator(
            _keyProvider, Microsoft.Extensions.Options.Options.Create(Options), new FixedTimeProvider(Now));
    }

    public void Dispose() => _keyProvider.Dispose();

    private static ConfigurationJwtSigningKeyProvider CreateKeyProvider()
    {
        using var rsa = RSA.Create(2048);
        var pem = rsa.ExportRSAPrivateKeyPem();
        return new ConfigurationJwtSigningKeyProvider(
            Microsoft.Extensions.Options.Options.Create(new JwtSigningKeyOptions { PrivateKeyPem = pem }));
    }

    private static JwtAccessTokenRequest DefaultRequest(params string[] roles) => new(
        UserId: Guid.NewGuid(),
        TenantId: Guid.NewGuid(),
        SessionId: Guid.NewGuid(),
        Roles: roles);

    // ValidateLifetime is deliberately false: JsonWebTokenHandler checks
    // expiry against the real system clock, not the FixedTimeProvider these
    // tests inject into the generator — the token's Now is 2026-07-27, far
    // in the past relative to the real clock the validator reads, so
    // lifetime validation would always (correctly) reject it here.
    // Expiration/iat/nbf correctness is verified precisely and directly by
    // Iat_and_nbf_equal_the_time_provider_and_exp_equals_iat_plus_the_configured_lifetime
    // below, by inspecting the claims themselves rather than depending on
    // wall-clock time during test execution.
    private TokenValidationParameters ValidationParameters(RsaSecurityKey validationKey) => new()
    {
        ValidateIssuer = true,
        ValidIssuer = Options.Issuer,
        ValidateAudience = true,
        ValidAudience = Options.Audience,
        ValidateLifetime = false,
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = validationKey,
        ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
    };

    [Fact]
    public async Task Generated_token_validates_successfully_against_the_matching_public_key()
    {
        var request = DefaultRequest("ADMIN");

        var result = _generator.GenerateAccessToken(request);

        var validationKey = _keyProvider.GetValidationKeys().Single().SecurityKey;
        var validationResult = await _handler.ValidateTokenAsync(result.Token, ValidationParameters(validationKey));

        validationResult.IsValid.Should().BeTrue(validationResult.Exception?.ToString());
    }

    [Fact]
    public void Generated_token_header_declares_RS256()
    {
        var token = _generator.GenerateAccessToken(DefaultRequest("ADMIN")).Token;

        var jwt = new JsonWebToken(token);

        jwt.Alg.Should().Be(SecurityAlgorithms.RsaSha256);
    }

    [Fact]
    public void Generated_token_header_kid_matches_the_current_signing_keys_id()
    {
        var token = _generator.GenerateAccessToken(DefaultRequest("ADMIN")).Token;

        var jwt = new JsonWebToken(token);

        jwt.Kid.Should().Be(_keyProvider.GetCurrentSigningKey().KeyId);
    }

    [Fact]
    public void Generated_token_carries_every_expected_claim_with_the_correct_value()
    {
        var request = DefaultRequest("ADMIN");

        var token = _generator.GenerateAccessToken(request).Token;
        var jwt = new JsonWebToken(token);

        jwt.Subject.Should().Be(request.UserId.ToString());
        jwt.GetClaim("tenant_id").Value.Should().Be(request.TenantId.ToString());
        jwt.GetClaim("session_id").Value.Should().Be(request.SessionId.ToString());
        jwt.Id.Should().NotBeNullOrWhiteSpace(); // jti
        jwt.Issuer.Should().Be(Options.Issuer);
        jwt.Audiences.Should().ContainSingle(Options.Audience);
    }

    [Fact]
    public void Generated_token_supports_multiple_roles()
    {
        var request = DefaultRequest("ADMIN", "OPERATOR");

        var token = _generator.GenerateAccessToken(request).Token;
        var jwt = new JsonWebToken(token);

        var roles = jwt.GetPayloadValue<string[]>("role");
        roles.Should().BeEquivalentTo(["ADMIN", "OPERATOR"]);
    }

    [Fact]
    public void Generated_token_supports_a_single_role_still_encoded_as_an_array()
    {
        var token = _generator.GenerateAccessToken(DefaultRequest("ADMIN")).Token;
        var jwt = new JsonWebToken(token);

        var roles = jwt.GetPayloadValue<string[]>("role");
        roles.Should().Equal("ADMIN");
    }

    [Fact]
    public void Generated_token_never_includes_a_permissions_claim()
    {
        var token = _generator.GenerateAccessToken(DefaultRequest("ADMIN")).Token;
        var jwt = new JsonWebToken(token);

        jwt.TryGetClaim("permissions", out _).Should().BeFalse();
    }

    [Fact]
    public void Generated_token_contains_only_the_documented_claim_set()
    {
        var token = _generator.GenerateAccessToken(DefaultRequest("ADMIN")).Token;
        var jwt = new JsonWebToken(token);

        var claimTypes = jwt.Claims.Select(c => c.Type).ToHashSet();

        claimTypes.Should().BeSubsetOf(
            [JwtRegisteredClaimNames.Sub, "tenant_id", "session_id", JwtRegisteredClaimNames.Jti, "role",
                JwtRegisteredClaimNames.Iat, JwtRegisteredClaimNames.Nbf, JwtRegisteredClaimNames.Exp,
                JwtRegisteredClaimNames.Iss, JwtRegisteredClaimNames.Aud]);
    }

    [Fact]
    public void Iat_and_nbf_equal_the_time_provider_and_exp_equals_iat_plus_the_configured_lifetime()
    {
        var token = _generator.GenerateAccessToken(DefaultRequest("ADMIN")).Token;
        var jwt = new JsonWebToken(token);

        jwt.IssuedAt.Should().Be(Now.UtcDateTime);
        jwt.ValidFrom.Should().Be(Now.UtcDateTime); // nbf
        jwt.ValidTo.Should().Be(Now.UtcDateTime.Add(Options.AccessTokenLifetime));
    }

    [Fact]
    public void GenerateAccessToken_returns_an_ExpiresAt_matching_the_token_itself()
    {
        var result = _generator.GenerateAccessToken(DefaultRequest("ADMIN"));

        result.ExpiresAt.Should().Be(new DateTimeOffset(Now.UtcDateTime.Add(Options.AccessTokenLifetime), TimeSpan.Zero));
    }

    [Fact]
    public async Task Token_signed_by_one_key_does_not_validate_against_a_different_keys_public_half()
    {
        using var otherKeyProvider = CreateKeyProvider();
        var token = _generator.GenerateAccessToken(DefaultRequest("ADMIN")).Token;

        var otherValidationKey = otherKeyProvider.GetValidationKeys().Single().SecurityKey;
        var validationResult = await _handler.ValidateTokenAsync(token, ValidationParameters(otherValidationKey));

        validationResult.IsValid.Should().BeFalse();
        validationResult.Exception.Should().NotBeNull();
    }

    [Fact]
    public async Task Concurrent_calls_from_the_same_generator_all_succeed_with_unique_jtis_and_valid_signatures()
    {
        var validationKey = _keyProvider.GetValidationKeys().Single().SecurityKey;

        var results = await Task.WhenAll(Enumerable.Range(0, 200).Select(_ =>
            Task.Run(() => _generator.GenerateAccessToken(DefaultRequest("ADMIN")))));

        results.Select(r => r.Jti).Distinct().Should().HaveCount(200);

        foreach (var result in results)
        {
            var validationResult = await _handler.ValidateTokenAsync(result.Token, ValidationParameters(validationKey));
            validationResult.IsValid.Should().BeTrue(validationResult.Exception?.ToString());
        }
    }

    [Fact]
    public void ToString_never_includes_the_access_token()
    {
        var result = _generator.GenerateAccessToken(DefaultRequest("ADMIN"));

        var text = result.ToString();

        text.Should().NotContain(result.Token);
        text.Should().Contain("[REDACTED]");
        text.Should().Contain(result.Jti); // non-sensitive, remains visible for correlation
    }
}
