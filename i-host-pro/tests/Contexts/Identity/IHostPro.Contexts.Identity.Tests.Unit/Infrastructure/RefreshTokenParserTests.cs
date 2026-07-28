using FluentAssertions;
using IHostPro.Contexts.Identity.Infrastructure.Security;

namespace IHostPro.Contexts.Identity.Tests.Unit.Infrastructure;

public class RefreshTokenParserTests
{
    private static readonly RefreshTokenParser Parser = new();

    private static string ValidToken(Guid? tenantId = null, Guid? tokenId = null, string? secret = null) =>
        $"{tenantId ?? Guid.NewGuid():N}.{tokenId ?? Guid.NewGuid():N}.{secret ?? "abcDEF012-_"}";

    [Fact]
    public void TryParse_accepts_a_well_formed_token_and_extracts_tenantId_and_tokenId()
    {
        var tenantId = Guid.NewGuid();
        var tokenId = Guid.NewGuid();

        var result = Parser.TryParse(ValidToken(tenantId, tokenId), out var parsed);

        result.Should().BeTrue();
        parsed.TenantId.Should().Be(tenantId);
        parsed.TokenId.Should().Be(tokenId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void TryParse_rejects_null_or_empty_input(string? input)
    {
        Parser.TryParse(input, out _).Should().BeFalse();
    }

    [Fact]
    public void TryParse_rejects_whitespace_only_input()
    {
        Parser.TryParse("   ", out _).Should().BeFalse();
    }

    [Fact]
    public void TryParse_rejects_leading_whitespace()
    {
        Parser.TryParse(" " + ValidToken(), out _).Should().BeFalse();
    }

    [Fact]
    public void TryParse_rejects_trailing_whitespace()
    {
        Parser.TryParse(ValidToken() + " ", out _).Should().BeFalse();
    }

    [Fact]
    public void TryParse_rejects_whitespace_embedded_in_the_secret_segment()
    {
        Parser.TryParse(ValidToken(secret: "abc def"), out _).Should().BeFalse();
    }

    [Fact]
    public void TryParse_rejects_too_few_segments()
    {
        var token = $"{Guid.NewGuid():N}.{Guid.NewGuid():N}";

        Parser.TryParse(token, out _).Should().BeFalse();
    }

    [Fact]
    public void TryParse_rejects_extra_segments()
    {
        var token = ValidToken() + ".extra";

        Parser.TryParse(token, out _).Should().BeFalse();
    }

    [Fact]
    public void TryParse_rejects_an_empty_middle_segment()
    {
        var token = $"{Guid.NewGuid():N}..abc";

        Parser.TryParse(token, out _).Should().BeFalse();
    }

    [Fact]
    public void TryParse_rejects_an_uppercase_guid_segment()
    {
        var token = $"{Guid.NewGuid():N}".ToUpperInvariant() + $".{Guid.NewGuid():N}.abc";

        Parser.TryParse(token, out _).Should().BeFalse();
    }

    [Fact]
    public void TryParse_rejects_a_hyphenated_guid_segment()
    {
        var token = $"{Guid.NewGuid()}.{Guid.NewGuid():N}.abc"; // default ToString() includes hyphens

        Parser.TryParse(token, out _).Should().BeFalse();
    }

    [Fact]
    public void TryParse_rejects_a_braced_guid_segment()
    {
        var token = $"{{{Guid.NewGuid():N}}}.{Guid.NewGuid():N}.abc";

        Parser.TryParse(token, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData(31)]
    [InlineData(33)]
    public void TryParse_rejects_a_guid_segment_of_the_wrong_length(int length)
    {
        var badSegment = new string('a', length);
        var token = $"{badSegment}.{Guid.NewGuid():N}.abc";

        Parser.TryParse(token, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("abc+def")]
    [InlineData("abc/def")]
    [InlineData("abc def")]
    public void TryParse_rejects_a_secret_segment_outside_the_base64url_alphabet(string secret)
    {
        Parser.TryParse(ValidToken(secret: secret), out _).Should().BeFalse();
    }

    [Fact]
    public void TryParse_rejects_base64_padding_in_the_secret_segment()
    {
        Parser.TryParse(ValidToken(secret: "abcdef=="), out _).Should().BeFalse();
    }

    [Fact]
    public void TryParse_rejects_an_empty_secret_segment()
    {
        var token = $"{Guid.NewGuid():N}.{Guid.NewGuid():N}.";

        Parser.TryParse(token, out _).Should().BeFalse();
    }

    [Fact]
    public void TryParse_accepts_the_full_base64url_alphabet_including_hyphen_and_underscore()
    {
        Parser.TryParse(ValidToken(secret: "AZaz09-_"), out var parsed).Should().BeTrue();
    }

    [Fact]
    public void TryParse_rejects_input_longer_than_the_defensive_maximum()
    {
        var oversizedSecret = new string('A', 1000);

        Parser.TryParse(ValidToken(secret: oversizedSecret), out _).Should().BeFalse();
    }

    [Fact]
    public void TryParse_accepts_the_longest_legitimate_token_for_the_maximum_configured_secret_size()
    {
        // 64 bytes (RefreshTokenOptions' own maximum, Etapa 4) -> 86 base64url characters.
        var secret = new string('A', 86);

        Parser.TryParse(ValidToken(secret: secret), out _).Should().BeTrue();
    }

    [Fact]
    public void TryParse_does_not_mutate_the_out_parameter_on_failure()
    {
        Parser.TryParse("clearly not a token", out var parsed).Should().BeFalse();

        parsed.Should().Be(default(IHostPro.Contexts.Identity.Application.ParsedRefreshToken));
    }
}
