using IHostPro.Contexts.Identity.Domain;

namespace IHostPro.Contexts.Identity.Application;

/// <summary>
/// Stages a <see cref="SecurityAuditEntry"/> for persistence within the
/// current tenant-aware transaction (Incremento 2 plan, Etapa 9). Only ever
/// called for an already-resolved tenant — see
/// <see cref="SecurityAuditEntry"/> for why events that occur before that
/// point are never recorded here.
///
/// Never calls <c>SaveChangesAsync</c> itself — matches
/// <c>UserStore</c>'s established pattern (Incremento 1): the single
/// <c>SaveChangesAsync</c> for the whole use case happens exclusively inside
/// <c>ITenantAwareUnitOfWork.ExecuteAsync</c>.
/// </summary>
public interface ISecurityAuditWriter
{
    void Record(SecurityAuditEntry entry);
}
