using IHostPro.Contexts.PropertyManagement.Domain;

namespace IHostPro.Contexts.PropertyManagement.Application;

/// <summary>
/// Stages a <see cref="PropertyAuditEntry"/> for persistence within the
/// current tenant-aware transaction — mirrors Identity's
/// <c>ISecurityAuditWriter</c> exactly. Never calls <c>SaveChangesAsync</c>
/// itself: the single save for the whole use case happens inside
/// <see cref="IPropertyManagementTransactionExecutor"/>.
/// </summary>
public interface IPropertyAuditWriter
{
    void Record(PropertyAuditEntry entry);
}
