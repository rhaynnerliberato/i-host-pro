namespace IHostPro.Contexts.Configuration.Application.Policies;

/// <summary>A read-only projection of a <c>PolicyDefinition</c> catalog entry — never exposes a way to create/alter/remove one (the catalog is seed-only).</summary>
public sealed record PolicyDefinitionResult(
    string Code, string Name, string Description, string Category, string ValueType, int SchemaVersion, bool IsActive);
