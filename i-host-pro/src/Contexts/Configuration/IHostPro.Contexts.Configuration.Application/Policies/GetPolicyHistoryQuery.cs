using IHostPro.BuildingBlocks.Application;

namespace IHostPro.Contexts.Configuration.Application.Policies;

/// <summary>Every version (current and superseded) of a policy value at an exact scope, ordered newest-first.</summary>
public sealed record GetPolicyHistoryQuery(
    Guid TenantId, string PolicyCode, string ScopeType, Guid? PropertyId)
    : IQuery<IReadOnlyList<PolicyValueDetailResult>>;
