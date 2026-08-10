using IHostPro.BuildingBlocks.Domain;

namespace IHostPro.Contexts.Configuration.Domain;

/// <summary>
/// The platform's fixed policy catalog entry (Fase 5, Incremento 1 official
/// decision 6: "Policy... implementa apenas... PolicyDefinition"). Identified
/// by its stable <see cref="Entity{TId}.Id"/> code (e.g. <c>EARLY_CHECKIN</c>).
/// A system catalog, not tenant-owned — carries no <c>TenantId</c> and is
/// never mapped under Row-Level Security, same convention already used by
/// Identity's own <c>Permission</c> catalog. A tenant never creates, removes
/// or alters a <see cref="PolicyDefinition"/> — this Bounded Context exposes
/// no command to do so; the catalog is seeded via EF Core migration data
/// (<c>ConfigurationCatalogSeed</c>, Infrastructure) exclusively.
/// </summary>
public sealed class PolicyDefinition : Entity<string>
{
    public string Name { get; private set; } = null!;
    public string Description { get; private set; } = null!;
    public string Category { get; private set; } = null!;
    public PolicyValueType ValueType { get; private set; }
    public int SchemaVersion { get; private set; }
    public bool IsActive { get; private set; }

    private PolicyDefinition()
    {
        // EF Core materialization.
    }

    public PolicyDefinition(
        string code, string name, string description, string category,
        PolicyValueType valueType, int schemaVersion, bool isActive)
        : base(code)
    {
        if (string.IsNullOrWhiteSpace(code))
            throw new ArgumentException("Policy code cannot be empty.", nameof(code));
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Policy name cannot be empty.", nameof(name));
        if (string.IsNullOrWhiteSpace(description))
            throw new ArgumentException("Policy description cannot be empty.", nameof(description));
        if (string.IsNullOrWhiteSpace(category))
            throw new ArgumentException("Policy category cannot be empty.", nameof(category));
        if (schemaVersion < 1)
            throw new ArgumentOutOfRangeException(nameof(schemaVersion), schemaVersion, "Schema version must be at least 1.");

        Name = name;
        Description = description;
        Category = category;
        ValueType = valueType;
        SchemaVersion = schemaVersion;
        IsActive = isActive;
    }
}
