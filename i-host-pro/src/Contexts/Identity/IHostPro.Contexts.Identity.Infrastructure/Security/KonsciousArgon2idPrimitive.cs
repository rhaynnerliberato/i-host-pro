using System.Text;
using Konscious.Security.Cryptography;

namespace IHostPro.Contexts.Identity.Infrastructure.Security;

/// <summary>
/// Argon2id primitive backed by Konscious.Security.Cryptography.Argon2 (MIT,
/// fully managed — no native binary dependency). Chosen over the native
/// libsodium-backed alternative (NSec.Cryptography) specifically to avoid
/// introducing an unvalidated native-binary deployment variable in an
/// environment where Docker execution has never been confirmed
/// (Incremento 1 plan, adendo final, Section 5). The maintenance risk of a
/// single-maintainer, infrequently-released package is accepted and
/// documented, mirroring the treatment already given to Wolverine (ADR-004).
/// </summary>
public sealed class KonsciousArgon2idPrimitive : IArgon2idPrimitive
{
    public byte[] Hash(string password, byte[] salt, int memoryKib, int iterations, int parallelism, int hashSizeBytes)
    {
        using var argon2 = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            DegreeOfParallelism = parallelism,
            Iterations = iterations,
            MemorySize = memoryKib,
        };

        return argon2.GetBytes(hashSizeBytes);
    }
}
