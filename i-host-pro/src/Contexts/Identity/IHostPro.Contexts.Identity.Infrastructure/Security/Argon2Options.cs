namespace IHostPro.Contexts.Identity.Infrastructure.Security;

/// <summary>
/// Argon2id parameters, externalized to configuration (never hardcoded —
/// Documento 15 §24) so they can be tuned per environment and can evolve
/// without a code change. Defaults follow OWASP's minimum recommendation for
/// Argon2id (m=19 MiB, t=2, p=1).
/// </summary>
public sealed class Argon2Options
{
    public const string SectionName = "Identity:Argon2";

    public int MemoryKib { get; set; } = 19_456;
    public int Iterations { get; set; } = 2;
    public int Parallelism { get; set; } = 1;
    public int SaltSizeBytes { get; set; } = 16;
    public int HashSizeBytes { get; set; } = 32;
}
