namespace IHostPro.Contexts.Configuration.Application.Policies;

/// <summary>
/// A read-only projection of a single <c>PolicyValue</c> row — <see cref="Value"/>
/// is the raw stored JSON (never re-typed per policy code), used only by
/// this context's own administrative API for "what is set at this exact
/// scope"/"history" queries — never confused with the resolved, typed shape
/// <see cref="EffectivePolicyResult"/> returns.
/// </summary>
public sealed record PolicyValueDetailResult(
    Guid Id, string PolicyCode, string ScopeType, Guid? ScopeReferenceId, int Version,
    string Value, DateTimeOffset CreatedAtUtc, Guid CreatedByUserId, string Reason, bool IsCurrent);
