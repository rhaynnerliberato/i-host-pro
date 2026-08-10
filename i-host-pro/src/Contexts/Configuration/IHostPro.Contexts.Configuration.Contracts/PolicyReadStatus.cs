namespace IHostPro.Contexts.Configuration.Contracts;

/// <summary>
/// The outcome of a successful call to the policy engine — Fase 5,
/// Incremento 1 official decision 4. Deliberately only two members:
/// engine unavailability (timeout, database/cache failure, unexpected
/// error, engine down) is never represented here — it is a distinct,
/// typed failure (<see cref="PolicyEngineUnavailableException"/>), never a
/// value of this enum, so a consumer can never mistake "the engine could
/// not answer" for "the engine answered: nothing is configured."
/// </summary>
public enum PolicyReadStatus
{
    /// <summary>A value was found at <see cref="PolicyResolvedScope.Property"/>, <see cref="PolicyResolvedScope.Tenant"/> or <see cref="PolicyResolvedScope.Global"/>.</summary>
    Resolved,

    /// <summary>The engine answered correctly; no value exists at any scope.</summary>
    NotConfigured,
}
