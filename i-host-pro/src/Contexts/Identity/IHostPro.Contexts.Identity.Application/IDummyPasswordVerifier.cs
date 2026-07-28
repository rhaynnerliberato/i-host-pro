namespace IHostPro.Contexts.Identity.Application;

/// <summary>
/// Runs a real Argon2id verification against a fixed dummy hash whenever a
/// login is rejected before ever reaching a real password check — mitigating
/// a timing side-channel that would otherwise let an attacker distinguish
/// "tenant/user does not exist" (fails in microseconds, no Argon2id at all)
/// from "user exists, wrong password" (fails only after Argon2id's
/// deliberately expensive, memory-hard computation) purely from response
/// latency, even though both produce an identical external response
/// (Incremento 2 plan, Etapa 9).
///
/// Technical feasibility confirmed before implementing (not assumed):
/// <c>Argon2PasswordHasher.HashPassword</c>/<c>VerifyHashedPassword</c> never
/// dereference their <c>User</c> parameter — verified by reading the actual
/// implementation — so a dummy verification needs no real user or stored
/// hash, only a hash computed once with the currently configured Argon2
/// parameters (so its cost matches the real path exactly).
/// </summary>
public interface IDummyPasswordVerifier
{
    /// <summary>
    /// Costs approximately the same as a real, failed password check. The
    /// result is intentionally discarded — this exists purely to consume
    /// time, never to authenticate anything.
    /// </summary>
    void Verify(string submittedPassword);
}
