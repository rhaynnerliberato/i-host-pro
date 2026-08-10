namespace IHostPro.Contexts.Configuration.Infrastructure.Resolution;

/// <summary>
/// Mirrors <see cref="IHostPro.Contexts.Configuration.Domain.PolicyScopeType"/>
/// but lives in Infrastructure, not Contracts — the internal resolver
/// (<see cref="IPolicyValueResolver"/>) is not allowed to leak a Domain type
/// across the Contracts boundary, and Contracts is not allowed to depend on
/// Domain at all.
/// </summary>
internal enum ResolvedScopeKind
{
    Property,
    Tenant,
    Global,
}
