namespace IHostPro.Contexts.Communication.Domain;

/// <summary>
/// What <see cref="Message.ApplyProviderStatus"/> actually did (Fase 9,
/// Checkpoint 2.3.3). <see cref="Applied"/> means the aggregate's state was
/// mutated; <see cref="Duplicate"/>/<see cref="Regression"/> are both
/// idempotent no-ops (mandate §19/§20 — never an exception) — the caller
/// uses this to choose which structured audit event to emit (mandate §27).
/// </summary>
public enum ProviderStatusApplicationResult
{
    Applied,
    Duplicate,
    Regression,
}
