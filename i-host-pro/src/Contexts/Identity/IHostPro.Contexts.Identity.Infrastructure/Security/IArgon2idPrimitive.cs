namespace IHostPro.Contexts.Identity.Infrastructure.Security;

/// <summary>
/// Isolates the concrete Argon2id library from the rest of Infrastructure
/// (Incremento 1 plan, "Estratégia de troca futura sem invalidar hashes").
/// Swapping the underlying library (e.g. Konscious for a future native
/// binding) only requires a new implementation of this interface — the PHC
/// string format owned by <see cref="Argon2PhcParser"/> is unaffected, since
/// Argon2id is a standardized algorithm (RFC 9106) and any compliant
/// implementation reproduces the same output for the same inputs.
/// </summary>
public interface IArgon2idPrimitive
{
    byte[] Hash(string password, byte[] salt, int memoryKib, int iterations, int parallelism, int hashSizeBytes);
}
