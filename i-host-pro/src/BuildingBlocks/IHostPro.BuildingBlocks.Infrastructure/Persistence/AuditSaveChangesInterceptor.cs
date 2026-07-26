using Microsoft.EntityFrameworkCore.Diagnostics;

namespace IHostPro.BuildingBlocks.Infrastructure.Persistence;

/// <summary>
/// Generic EF Core interceptor that will record an audit trail entry for every
/// relevant entity change, regardless of which Bounded Context produced it
/// (Documento 11 §23 — "a auditoria nunca poderá depender da interface").
/// This is a Phase 0 structural placeholder: it currently exposes the extension
/// point wired into every module's DbContext; the concrete audit-writing logic
/// (mapping tracked entities to immutable audit records) belongs to the Audit
/// Bounded Context and will be implemented when that context is built.
/// </summary>
public sealed class AuditSaveChangesInterceptor : SaveChangesInterceptor
{
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        // Intentionally left as an extension point for Phase 0. The Audit Bounded
        // Context will subscribe to the change tracker here to produce immutable
        // audit records, once it exists (see Architecture Principles, Section 12).
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }
}
