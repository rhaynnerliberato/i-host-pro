using IHostPro.Contexts.Configuration.Contracts;

namespace IHostPro.Contexts.Configuration.Application.Policies;

/// <summary>
/// The administrative API's own projection of a
/// <see cref="PolicyReadResult{TValue}"/> — <see cref="Value"/> is boxed as
/// <c>object?</c> because this query is generic over whichever policy code
/// the route names (<c>EarlyCheckInPolicy</c> or <c>LateCheckoutPolicy</c>),
/// resolved only at runtime; <c>System.Text.Json</c> serializes an
/// <c>object</c>-typed property using the value's actual runtime type.
/// </summary>
public sealed record EffectivePolicyResult(
    string PolicyCode, PolicyReadStatus Status, object? Value, PolicyResolvedScope? ResolvedScope, int? Version);
