using System.Security.Cryptography;

namespace IHostPro.TenantProvisioning;

/// <summary>
/// Generates a random initial admin password that deterministically
/// satisfies the real <c>PasswordPolicyOptions</c> defaults (min length 10,
/// requires digit/upper/lower/special - see
/// IHostPro.Contexts.Identity.Infrastructure.Security.PasswordPolicyOptions)
/// without ever accepting a caller-supplied value - CP5.3D-C corrective
/// Decision Gate item 14: the credential must never pass through
/// stdout/logs/config/args/shell history, so it can only ever be generated
/// in-process and written directly to Secrets Manager (see Program.cs).
/// </summary>
public static class SecurePasswordGenerator
{
    private const string Uppercase = "ABCDEFGHJKLMNPQRSTUVWXYZ"; // no I/O - avoids visual ambiguity
    private const string Lowercase = "abcdefghijkmnopqrstuvwxyz";
    private const string Digits = "23456789";
    private const string Special = "!@#$%^&*-_=+";
    private const string AllCharacters = Uppercase + Lowercase + Digits + Special;

    public static string Generate(int length = 32)
    {
        if (length < 10)
            throw new ArgumentOutOfRangeException(nameof(length), "Length must be at least the password policy minimum (10).");

        // Guarantee at least one character from each required category (never
        // left to chance), then fill the remainder randomly, then shuffle -
        // a standard secure-random-password construction.
        var chars = new char[length];
        chars[0] = PickRandom(Uppercase);
        chars[1] = PickRandom(Lowercase);
        chars[2] = PickRandom(Digits);
        chars[3] = PickRandom(Special);

        for (var i = 4; i < length; i++)
            chars[i] = PickRandom(AllCharacters);

        Shuffle(chars);

        return new string(chars);
    }

    private static char PickRandom(string pool) => pool[RandomNumberGenerator.GetInt32(pool.Length)];

    private static void Shuffle(char[] chars)
    {
        for (var i = chars.Length - 1; i > 0; i--)
        {
            var j = RandomNumberGenerator.GetInt32(i + 1);
            (chars[i], chars[j]) = (chars[j], chars[i]);
        }
    }
}
