using IHostPro.BuildingBlocks.Application;

namespace IHostPro.Contexts.Configuration.Application.Policies;

/// <summary>Lists the seeded policy catalog — no tenant-specific filtering, the catalog is platform-wide.</summary>
public sealed record ListPolicyDefinitionsQuery : IQuery<IReadOnlyList<PolicyDefinitionResult>>;
