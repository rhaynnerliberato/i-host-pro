using System.Text.RegularExpressions;
using FluentAssertions;
using IHostPro.Contexts.Identity.Infrastructure.Security;

namespace IHostPro.Contexts.Identity.Tests.Unit.Infrastructure;

public partial class RefreshTokenHasherTests
{
    private static readonly RefreshTokenHasher Hasher = new();

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex LowercaseHex64Regex();

    [Fact]
    public void ComputeHash_returns_64_lowercase_hex_characters()
    {
        var hash = Hasher.ComputeHash("some-token-value.abc.def");

        LowercaseHex64Regex().IsMatch(hash).Should().BeTrue();
    }

    [Fact]
    public void ComputeHash_is_deterministic_for_the_same_input()
    {
        const string token = "tenant.token.secret";

        Hasher.ComputeHash(token).Should().Be(Hasher.ComputeHash(token));
    }

    [Theory]
    [InlineData("aaaa.bbbb.cccc", "Xaaa.bbbb.cccc")] // first (tenant) segment changed
    [InlineData("aaaa.bbbb.cccc", "aaaa.Xbbb.cccc")] // second (tokenId) segment changed
    [InlineData("aaaa.bbbb.cccc", "aaaa.bbbb.Xccc")] // third (secret) segment changed
    [InlineData("aaaa.bbbb.cccc", "aaaa.bbbb.ccccX")] // single trailing character appended
    public void ComputeHash_changes_when_any_segment_changes(string original, string modified)
    {
        Hasher.ComputeHash(original).Should().NotBe(Hasher.ComputeHash(modified));
    }

    [Fact]
    public void Verify_returns_true_for_a_matching_token_and_hash()
    {
        const string token = "tenant.token.secret";
        var hash = Hasher.ComputeHash(token);

        Hasher.Verify(token, hash).Should().BeTrue();
    }

    [Fact]
    public void Verify_returns_false_for_a_tampered_token()
    {
        const string token = "tenant.token.secret";
        var hash = Hasher.ComputeHash(token);

        Hasher.Verify("tenant.token.SECRET", hash).Should().BeFalse();
    }

    [Fact]
    public void Verify_returns_false_for_an_unrelated_hash()
    {
        var unrelatedHash = Hasher.ComputeHash("something-completely-different");

        Hasher.Verify("tenant.token.secret", unrelatedHash).Should().BeFalse();
    }

    [Fact]
    public void Verify_returns_false_rather_than_throwing_when_hash_lengths_differ()
    {
        Hasher.Verify("tenant.token.secret", "too-short-to-be-a-real-hash").Should().BeFalse();
    }

    [Fact]
    public void ComputeHash_null_argument_exception_never_contains_a_token_value()
    {
        var act = () => Hasher.ComputeHash(null!);

        act.Should().Throw<ArgumentException>()
            .Which.Message.Should().NotContain("token");
    }

    [Fact]
    public async Task Concurrent_hashing_of_many_different_tokens_produces_correct_independent_results()
    {
        var tokens = Enumerable.Range(0, 500).Select(i => $"tenant-{i}.token-{i}.secret-{i}").ToArray();

        var hashes = await Task.WhenAll(tokens.Select(t => Task.Run(() => Hasher.ComputeHash(t))));

        for (var i = 0; i < tokens.Length; i++)
            hashes[i].Should().Be(Hasher.ComputeHash(tokens[i]));

        hashes.Distinct().Should().HaveCount(tokens.Length);
    }
}
