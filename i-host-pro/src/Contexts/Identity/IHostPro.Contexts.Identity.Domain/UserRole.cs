using IHostPro.BuildingBlocks.Domain;

namespace IHostPro.Contexts.Identity.Domain;

/// <summary>
/// Assigns a platform role to a user within a tenant. Role assignment itself
/// (a command that creates rows of this type) is out of scope for Incremento 1
/// — this entity and its mapping exist so the schema/constraints can be
/// validated now; the assignment use case belongs to a later increment.
/// </summary>
public sealed class UserRole : ITenantOwned
{
    public Guid TenantId { get; private set; }
    public Guid UserId { get; private set; }
    public string RoleCode { get; private set; } = null!;
    public DateTimeOffset AssignedAt { get; private set; }
    public Guid? AssignedByUserId { get; private set; }

    private UserRole()
    {
        // EF Core materialization.
    }

    public UserRole(Guid tenantId, Guid userId, string roleCode, DateTimeOffset assignedAt, Guid? assignedByUserId)
    {
        if (string.IsNullOrWhiteSpace(roleCode))
            throw new ArgumentException("Role code cannot be empty.", nameof(roleCode));

        TenantId = tenantId;
        UserId = userId;
        RoleCode = roleCode;
        AssignedAt = assignedAt;
        AssignedByUserId = assignedByUserId;
    }
}
