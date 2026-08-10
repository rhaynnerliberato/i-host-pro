namespace IHostPro.Contexts.Configuration.Infrastructure.Resolution;

/// <summary>
/// The generic, PROPERTY → TENANT → GLOBAL precedence resolver shared by
/// every typed policy reader — Fase 5, Incremento 1 official decisions §5:
/// "manter o resolvedor genérico interno ao contexto." Deliberately
/// <c>internal</c> (compiler-enforced, not just a naming convention) — no
/// other Bounded Context, and nothing in Contracts, may ever see this
/// interface; only <c>EarlyCheckInPolicyReader</c>/<c>LateCheckoutPolicyReader</c>
/// (the two public, policy-specific readers) consume it.
/// </summary>
internal interface IPolicyValueResolver
{
    Task<PolicyValueResolution> ResolveAsync(
        Guid tenantId, string policyCode, Guid? propertyId, CancellationToken cancellationToken);
}
