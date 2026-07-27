namespace IHostPro.Contexts.Identity.Infrastructure.Security;

/// <summary>
/// A decoded/to-be-encoded standard Argon2id PHC hash
/// (<c>$argon2id$v=19$m=..,t=..,p=..$salt$hash</c>) — no proprietary segment.
/// Any RFC 9106-compliant Argon2id implementation can verify a hash this
/// platform produced, since every parameter needed to reproduce it is present
/// in the string itself (Incremento 1 plan, Section 4).
/// </summary>
public sealed record Argon2PhcHash(int MemoryKib, int Iterations, int Parallelism, byte[] Salt, byte[] Hash)
{
    public string Encode() =>
        $"$argon2id$v=19$m={MemoryKib},t={Iterations},p={Parallelism}$" +
        $"{Convert.ToBase64String(Salt).TrimEnd('=')}${Convert.ToBase64String(Hash).TrimEnd('=')}";
}
