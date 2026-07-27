using IHostPro.BuildingBlocks.Domain;

namespace IHostPro.Contexts.Identity.Domain;

/// <summary>
/// Platform-defined permission catalog (Documento 09 §12-15), identified by a
/// stable code in the form <c>RESOURCE:LEVEL</c> or
/// <c>RESOURCE:LEVEL:SCOPE</c>. Seeded as stable identifiers even for resources
/// whose owning Bounded Context does not exist yet — future contexts consume
/// these codes, never invent new ones (Incremento 1 plan, Section 9-10).
/// </summary>
public sealed class Permission : Entity<string>
{
    public string Resource { get; private set; } = null!;
    public string Action { get; private set; } = null!;
    public string? Scope { get; private set; }

    private Permission()
    {
        // EF Core materialization.
    }

    public Permission(string code, string resource, string action, string? scope = null) : base(code)
    {
        if (string.IsNullOrWhiteSpace(resource))
            throw new ArgumentException("Resource cannot be empty.", nameof(resource));
        if (string.IsNullOrWhiteSpace(action))
            throw new ArgumentException("Action cannot be empty.", nameof(action));

        Resource = resource;
        Action = action;
        Scope = scope;
    }
}
