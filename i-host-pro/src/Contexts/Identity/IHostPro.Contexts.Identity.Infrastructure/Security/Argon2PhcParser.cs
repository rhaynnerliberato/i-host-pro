using System.Globalization;

namespace IHostPro.Contexts.Identity.Infrastructure.Security;

/// <summary>
/// Deterministic, <see cref="string.Split(char[])"/>-based parser/encoder for
/// the standard Argon2id PHC string. The main parsing logic does not rely on
/// a single monolithic regex — each segment is split explicitly and validated
/// on its own (Incremento 1 plan, Section 4/adendo final, Section 2). Hard,
/// non-configurable upper bounds on memory/iterations/parallelism/salt/hash
/// size protect against a manipulated hash string forcing excessive resource
/// consumption during verification — any malformed or out-of-bounds input
/// simply fails to parse, which the caller treats identically to a failed
/// password verification.
/// </summary>
public static class Argon2PhcParser
{
    private const int MaxMemoryKib = 1_048_576; // 1 GiB
    private const int MaxIterations = 10;
    private const int MaxParallelism = 8;
    private const int MinSaltBytes = 16;
    private const int MaxSaltBytes = 64;
    private const int MinHashBytes = 16;
    private const int MaxHashBytes = 64;

    public static bool TryParse(string? encoded, out Argon2PhcHash? hash)
    {
        hash = null;

        if (string.IsNullOrEmpty(encoded))
            return false;

        // "$argon2id$v=19$m=..,t=..,p=..$salt$hash" splits into 6 segments,
        // the first being the empty string before the leading '$'.
        var segments = encoded.Split('$');
        if (segments.Length != 6)
            return false;

        if (segments[0].Length != 0)
            return false;

        if (segments[1] != "argon2id")
            return false;

        if (segments[2] != "v=19")
            return false;

        if (!TryParseParameters(segments[3], out var memoryKib, out var iterations, out var parallelism))
            return false;

        if (!TryDecodeUnpaddedBase64(segments[4], MinSaltBytes, MaxSaltBytes, out var salt) || salt is null)
            return false;

        if (!TryDecodeUnpaddedBase64(segments[5], MinHashBytes, MaxHashBytes, out var hashBytes) || hashBytes is null)
            return false;

        hash = new Argon2PhcHash(memoryKib, iterations, parallelism, salt, hashBytes);
        return true;
    }

    private static bool TryParseParameters(string segment, out int memoryKib, out int iterations, out int parallelism)
    {
        memoryKib = 0;
        iterations = 0;
        parallelism = 0;

        var parts = segment.Split(',');
        if (parts.Length != 3)
            return false;

        if (!TryParseNamedInt(parts[0], "m", out memoryKib) || memoryKib < 1 || memoryKib > MaxMemoryKib)
            return false;

        if (!TryParseNamedInt(parts[1], "t", out iterations) || iterations < 1 || iterations > MaxIterations)
            return false;

        if (!TryParseNamedInt(parts[2], "p", out parallelism) || parallelism < 1 || parallelism > MaxParallelism)
            return false;

        return true;
    }

    private static bool TryParseNamedInt(string part, string expectedName, out int value)
    {
        value = 0;

        var equalsIndex = part.IndexOf('=');
        if (equalsIndex < 0)
            return false;

        var name = part[..equalsIndex];
        var rawValue = part[(equalsIndex + 1)..];

        if (name != expectedName)
            return false;

        return int.TryParse(rawValue, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryDecodeUnpaddedBase64(string segment, int minBytes, int maxBytes, out byte[]? bytes)
    {
        bytes = null;

        if (string.IsNullOrEmpty(segment))
            return false;

        // The PHC string format omits base64 padding; restore it before
        // decoding, since Convert.FromBase64String requires it.
        var remainder = segment.Length % 4;
        var padded = remainder == 0 ? segment : segment + new string('=', 4 - remainder);

        try
        {
            bytes = Convert.FromBase64String(padded);
        }
        catch (FormatException)
        {
            return false;
        }

        return bytes.Length >= minBytes && bytes.Length <= maxBytes;
    }
}
