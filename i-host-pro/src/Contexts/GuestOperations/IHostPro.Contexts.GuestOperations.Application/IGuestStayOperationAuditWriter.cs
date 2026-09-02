using IHostPro.Contexts.GuestOperations.Domain;

namespace IHostPro.Contexts.GuestOperations.Application;

/// <summary>
/// Stages a <see cref="GuestStayOperationAuditEntry"/> for persistence
/// within the current tenant-aware transaction (Fase 12, Checkpoint 4 —
/// Guest Access Durable Audit Decision Gate) — mirrors
/// <c>Identity.Application.ISecurityAuditWriter</c>'s own established
/// pattern exactly. Never calls <c>SaveChangesAsync</c> itself — the single
/// <c>SaveChangesAsync</c> for the whole use case happens exclusively inside
/// <see cref="IGuestOperationsTransactionExecutor.ExecuteAsync{T}"/>.
/// </summary>
public interface IGuestStayOperationAuditWriter
{
    void Record(GuestStayOperationAuditEntry entry);
}
