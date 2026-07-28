using FluentAssertions;
using IHostPro.Contexts.Identity.Infrastructure.Security;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace IHostPro.Contexts.Identity.Tests.Unit.Infrastructure;

public class RefreshTokenGeneratorTests
{
    private static readonly RefreshTokenHasher Hasher = new();
    private static readonly RefreshTokenParser Parser = new();

    private static RefreshTokenGenerator CreateGenerator(int secretSizeBytes = 32) =>
        new(Hasher, Options.Create(new RefreshTokenOptions { SecretSizeBytes = secretSizeBytes }), TimeProvider.System);

    [Fact]
    public void Generate_produces_a_token_that_round_trips_through_the_parser()
    {
        var tenantId = Guid.NewGuid();
        var generator = CreateGenerator();

        var generated = generator.Generate(tenantId);

        Parser.TryParse(generated.Token, out var parsed).Should().BeTrue();
        parsed.TenantId.Should().Be(tenantId);
        parsed.TokenId.Should().Be(generated.TokenId);
    }

    [Fact]
    public void Generate_returns_a_TokenHash_matching_the_hasher_applied_to_the_full_token()
    {
        var generator = CreateGenerator();

        var generated = generator.Generate(Guid.NewGuid());

        generated.TokenHash.Should().Be(Hasher.ComputeHash(generated.Token));
    }

    [Fact]
    public void Generate_rejects_an_empty_tenant_id()
    {
        var generator = CreateGenerator();

        var act = () => generator.Generate(Guid.Empty);

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(16)]
    [InlineData(32)]
    [InlineData(64)]
    public void Generate_honors_the_configured_secret_size(int secretSizeBytes)
    {
        var generator = CreateGenerator(secretSizeBytes);

        var generated = generator.Generate(Guid.NewGuid());
        var secretSegment = generated.Token.Split('.')[2];
        var decodedSecret = Base64UrlEncoder.DecodeBytes(secretSegment);

        decodedSecret.Should().HaveCount(secretSizeBytes);
    }

    [Fact]
    public void Generate_produces_a_secret_segment_using_only_the_base64url_alphabet_without_padding()
    {
        var generator = CreateGenerator();

        var secretSegment = generator.Generate(Guid.NewGuid()).Token.Split('.')[2];

        secretSegment.Should().NotContain("+").And.NotContain("/").And.NotContain("=");
        secretSegment.Should().MatchRegex("^[A-Za-z0-9_-]+$");
    }

    [Fact]
    public void Generate_produces_lowercase_N_format_guid_segments()
    {
        var generator = CreateGenerator();
        var tenantId = Guid.NewGuid();

        var segments = generator.Generate(tenantId).Token.Split('.');

        segments[0].Should().Be(tenantId.ToString("N"));
        segments[0].Should().MatchRegex("^[0-9a-f]{32}$");
        segments[1].Should().MatchRegex("^[0-9a-f]{32}$");
    }

    [Fact]
    public void Generate_produces_exactly_three_segments()
    {
        var generator = CreateGenerator();

        generator.Generate(Guid.NewGuid()).Token.Split('.').Should().HaveCount(3);
    }

    [Fact]
    public async Task Concurrent_generation_never_produces_duplicate_tokens_ids_or_hashes()
    {
        var generator = CreateGenerator();
        var tenantId = Guid.NewGuid();

        var results = await Task.WhenAll(
            Enumerable.Range(0, 500).Select(_ => Task.Run(() => generator.Generate(tenantId))));

        results.Select(r => r.Token).Distinct().Should().HaveCount(500);
        results.Select(r => r.TokenId).Distinct().Should().HaveCount(500);
        results.Select(r => r.TokenHash).Distinct().Should().HaveCount(500);
    }

    [Fact]
    public void Two_consecutive_generations_never_produce_the_same_secret()
    {
        var generator = CreateGenerator();
        var tenantId = Guid.NewGuid();

        var first = generator.Generate(tenantId).Token.Split('.')[2];
        var second = generator.Generate(tenantId).Token.Split('.')[2];

        first.Should().NotBe(second);
    }

    [Fact]
    public void ToString_never_includes_the_token_or_its_hash()
    {
        var generator = CreateGenerator();

        var generated = generator.Generate(Guid.NewGuid());
        var text = generated.ToString();

        text.Should().NotContain(generated.Token);
        text.Should().NotContain(generated.TokenHash);
        text.Should().Contain("[REDACTED]");
        text.Should().Contain(generated.TokenId.ToString()); // non-sensitive, remains visible for correlation
    }
}
