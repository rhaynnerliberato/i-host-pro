namespace IHostPro.Contexts.Identity.Domain;

/// <summary>
/// Association row of the platform's fixed role -> permission catalog
/// (Incremento 1 plan, Section 10). Not tenant-owned.
/// </summary>
public sealed class RolePermission
{
    public string RoleCode { get; private set; } = null!;
    public string PermissionCode { get; private set; } = null!;

    private RolePermission()
    {
        // EF Core materialization.
    }

    public RolePermission(string roleCode, string permissionCode)
    {
        if (string.IsNullOrWhiteSpace(roleCode))
            throw new ArgumentException("Role code cannot be empty.", nameof(roleCode));
        if (string.IsNullOrWhiteSpace(permissionCode))
            throw new ArgumentException("Permission code cannot be empty.", nameof(permissionCode));

        RoleCode = roleCode;
        PermissionCode = permissionCode;
    }
}
