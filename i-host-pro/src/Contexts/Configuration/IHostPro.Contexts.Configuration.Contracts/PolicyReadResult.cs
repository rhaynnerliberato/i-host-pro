namespace IHostPro.Contexts.Configuration.Contracts;

/// <summary>
/// The typed result every policy reader returns (Fase 5, Incremento 1
/// official decisions §5): <see cref="Status"/>, <see cref="Value"/> (only
/// when <see cref="PolicyReadStatus.Resolved"/>), <see cref="ResolvedScope"/>
/// and <see cref="Version"/>. <see cref="Version"/> is <c>null</c> when
/// <see cref="ResolvedScope"/> is <see cref="PolicyResolvedScope.Global"/> —
/// GLOBAL values (official decision 2.2) are read-only, seed/migration-only,
/// and carry no version history, unlike TENANT/PROPERTY values which are
/// always versioned.
/// </summary>
public sealed class PolicyReadResult<TValue>
{
    public PolicyReadStatus Status { get; }
    public TValue? Value { get; }
    public PolicyResolvedScope? ResolvedScope { get; }
    public int? Version { get; }

    private PolicyReadResult(PolicyReadStatus status, TValue? value, PolicyResolvedScope? resolvedScope, int? version)
    {
        Status = status;
        Value = value;
        ResolvedScope = resolvedScope;
        Version = version;
    }

    public static PolicyReadResult<TValue> Resolved(TValue value, PolicyResolvedScope resolvedScope, int? version) =>
        new(PolicyReadStatus.Resolved, value, resolvedScope, version);

    public static PolicyReadResult<TValue> NotConfigured() =>
        new(PolicyReadStatus.NotConfigured, default, null, null);
}
