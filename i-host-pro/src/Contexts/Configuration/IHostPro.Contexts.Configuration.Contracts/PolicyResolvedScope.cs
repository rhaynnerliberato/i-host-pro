namespace IHostPro.Contexts.Configuration.Contracts;

/// <summary>
/// The scope level a <see cref="PolicyReadResult{TValue}"/> was resolved at,
/// following the PROPERTY → TENANT → GLOBAL precedence order (Fase 5,
/// Incremento 1 official decision 1). A separate, Contracts-local type from
/// <c>IHostPro.Contexts.Configuration.Domain.PolicyScopeType</c> — Contracts
/// must never depend on this context's own Domain project.
/// </summary>
public enum PolicyResolvedScope
{
    Property,
    Tenant,
    Global,
}
