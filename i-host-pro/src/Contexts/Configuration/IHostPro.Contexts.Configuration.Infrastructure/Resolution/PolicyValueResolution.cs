namespace IHostPro.Contexts.Configuration.Infrastructure.Resolution;

/// <summary>The raw (still-serialized) outcome of <see cref="IPolicyValueResolver.ResolveAsync"/>, before a typed reader deserializes <see cref="Value"/>.</summary>
internal sealed record PolicyValueResolution(bool Found, string? Value, ResolvedScopeKind? ScopeKind, int? Version);
