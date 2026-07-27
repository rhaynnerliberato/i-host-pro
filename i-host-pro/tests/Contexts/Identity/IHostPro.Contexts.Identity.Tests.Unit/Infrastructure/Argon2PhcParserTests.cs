using FluentAssertions;
using IHostPro.Contexts.Identity.Infrastructure.Security;

namespace IHostPro.Contexts.Identity.Tests.Unit.Infrastructure;

public class Argon2PhcParserTests
{
    [Fact]
    public void Encode_then_TryParse_round_trips_all_parameters()
    {
        var original = new Argon2PhcHash(19_456, 2, 1, new byte[16], new byte[32]);
        Random.Shared.NextBytes(original.Salt);
        Random.Shared.NextBytes(original.Hash);

        var encoded = original.Encode();
        var parsed = Argon2PhcParser.TryParse(encoded, out var result);

        parsed.Should().BeTrue();
        result!.MemoryKib.Should().Be(original.MemoryKib);
        result.Iterations.Should().Be(original.Iterations);
        result.Parallelism.Should().Be(original.Parallelism);
        result.Salt.Should().Equal(original.Salt);
        result.Hash.Should().Equal(original.Hash);
    }

    [Fact]
    public void Encode_produces_the_standard_PHC_format_with_no_proprietary_segment()
    {
        var hash = new Argon2PhcHash(19_456, 2, 1, new byte[16], new byte[32]);

        var encoded = hash.Encode();

        encoded.Should().MatchRegex(@"^\$argon2id\$v=19\$m=19456,t=2,p=1\$[A-Za-z0-9+/]+\$[A-Za-z0-9+/]+$");
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-phc-string")]
    [InlineData("$argon2id$v=19$m=1,t=1,p=1$salt")] // missing hash segment
    [InlineData("$argon2i$v=19$m=1,t=1,p=1$c2FsdA$aGFzaA")] // wrong algorithm
    [InlineData("$argon2id$v=18$m=1,t=1,p=1$c2FsdA$aGFzaA")] // wrong version
    [InlineData("$argon2id$v=19$m=1,t=1$c2FsdA$aGFzaA")] // missing parameter
    [InlineData("$argon2id$v=19$m=abc,t=1,p=1$c2FsdA$aGFzaA")] // non-numeric parameter
    [InlineData("$argon2id$v=19$m=1,t=1,p=1$not base64!!!$aGFzaA")] // malformed base64
    public void TryParse_rejects_malformed_input(string malformed)
    {
        var parsed = Argon2PhcParser.TryParse(malformed, out var result);

        parsed.Should().BeFalse();
        result.Should().BeNull();
    }

    [Theory]
    [InlineData(2_000_000)] // above the 1 GiB hard cap
    public void TryParse_rejects_memory_above_the_hard_cap(int memoryKib)
    {
        var malicious = $"$argon2id$v=19$m={memoryKib},t=2,p=1$c2FsdHNhbHRzYWx0c2FsdA$aGFzaGhhc2hoYXNoaGFzaGhhc2g";

        var parsed = Argon2PhcParser.TryParse(malicious, out _);

        parsed.Should().BeFalse();
    }

    [Theory]
    [InlineData(50)] // above the hard cap of 10
    public void TryParse_rejects_iterations_above_the_hard_cap(int iterations)
    {
        var malicious = $"$argon2id$v=19$m=19456,t={iterations},p=1$c2FsdHNhbHRzYWx0c2FsdA$aGFzaGhhc2hoYXNoaGFzaGhhc2g";

        var parsed = Argon2PhcParser.TryParse(malicious, out _);

        parsed.Should().BeFalse();
    }

    [Theory]
    [InlineData(100)] // above the hard cap of 8
    public void TryParse_rejects_parallelism_above_the_hard_cap(int parallelism)
    {
        var malicious = $"$argon2id$v=19$m=19456,t=2,p={parallelism}$c2FsdHNhbHRzYWx0c2FsdA$aGFzaGhhc2hoYXNoaGFzaGhhc2g";

        var parsed = Argon2PhcParser.TryParse(malicious, out _);

        parsed.Should().BeFalse();
    }

    [Fact]
    public void TryParse_rejects_a_salt_shorter_than_the_minimum()
    {
        // 4-byte salt, far below the 16-byte minimum.
        var malicious = "$argon2id$v=19$m=19456,t=2,p=1$AQIDBA$aGFzaGhhc2hoYXNoaGFzaGhhc2g";

        var parsed = Argon2PhcParser.TryParse(malicious, out _);

        parsed.Should().BeFalse();
    }
}
