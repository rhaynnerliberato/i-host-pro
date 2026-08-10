using IHostPro.Contexts.Configuration.Contracts;

namespace IHostPro.Contexts.Configuration.Infrastructure.Resolution;

/// <summary>Maps the internal <see cref="ResolvedScopeKind"/> to the public <see cref="PolicyResolvedScope"/> — the one point where this boundary crossing happens, shared by every typed reader.</summary>
internal static class PolicyResolvedScopeMapper
{
    public static PolicyResolvedScope ToContractScope(ResolvedScopeKind scopeKind) => scopeKind switch
    {
        ResolvedScopeKind.Property => PolicyResolvedScope.Property,
        ResolvedScopeKind.Tenant => PolicyResolvedScope.Tenant,
        ResolvedScopeKind.Global => PolicyResolvedScope.Global,
        _ => throw new ArgumentOutOfRangeException(nameof(scopeKind), scopeKind, "Unknown resolved scope kind."),
    };
}
