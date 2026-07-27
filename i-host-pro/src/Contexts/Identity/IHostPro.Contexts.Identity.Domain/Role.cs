using IHostPro.BuildingBlocks.Domain;

namespace IHostPro.Contexts.Identity.Domain;

/// <summary>
/// Platform-defined role catalog (Documento 09 §4: ADMIN, OPERATOR,
/// HOUSEKEEPER, PROPERTY_OWNER, SYSTEM, AI_AGENT, INTEGRATION). Identified by a
/// stable technical code, not a display name (Incremento 1 plan, Section 9).
/// Not tenant-owned — roles are fixed platform-wide in this phase; no
/// per-tenant customization or role creation exists yet (Documento 09 §18).
/// </summary>
public sealed class Role : Entity<string>
{
    public string DisplayName { get; private set; } = null!;

    private Role()
    {
        // EF Core materialization.
    }

    public Role(string code, string displayName) : base(code)
    {
        if (string.IsNullOrWhiteSpace(displayName))
            throw new ArgumentException("Display name cannot be empty.", nameof(displayName));

        DisplayName = displayName;
    }
}
