namespace IHostPro.Contexts.Configuration.Api.Contracts;

/// <summary>
/// <see cref="Status"/> is <c>"Resolved"</c>/<c>"NotConfigured"</c> — both a
/// legitimate, successful outcome (Fase 5, Incremento 1 official decision 4).
/// This endpoint returns 200 for both; it is never used to signal
/// unavailability, which surfaces as an unhandled exception instead (not one
/// of the seven documented ProblemDetails outcomes).
/// </summary>
public sealed record EffectivePolicyResponse(
    string PolicyCode, string Status, object? Value, string? ResolvedScope, int? Version);
