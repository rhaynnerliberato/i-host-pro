using IHostPro.BuildingBlocks.Application;

namespace IHostPro.Contexts.Configuration.Application.Policies;

/// <summary>
/// The current <c>PolicyValue</c> at an exact scope (never resolved
/// hierarchically) — <paramref name="ScopeType"/> is a raw string, never
/// <c>PolicyScopeType</c>: Api never references this context's own Domain
/// project.
/// </summary>
public sealed record GetPolicyValueByScopeQuery(
    Guid TenantId, string PolicyCode, string ScopeType, Guid? PropertyId)
    : IQuery<PolicyValueDetailResult>;
