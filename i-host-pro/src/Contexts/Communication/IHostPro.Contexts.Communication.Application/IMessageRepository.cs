using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.Communication.Domain;

namespace IHostPro.Contexts.Communication.Application;

public interface IMessageRepository : IRepository<Message, Guid>
{
    /// <summary>The single idempotency check this checkpoint needs (CP1 mandate §35) — a DB unique constraint on <c>IdempotencyKey</c> backstops this, mirrors ADR-018's own two-layer idempotency precedent.</summary>
    Task<Message?> GetByIdempotencyKeyAsync(string idempotencyKey, CancellationToken cancellationToken);

    /// <summary>
    /// Fase 9, Checkpoint 2.3.3 (ADR-022 item 14) — the webhook status
    /// consumer's own lookup. Tenant-scoped implicitly, same as every other
    /// method here (the Global Query Filter/RLS, never an explicit
    /// parameter) — the caller must set <c>ITenantContext</c> from the
    /// event's own TenantId first.
    ///
    /// Deliberately <c>SingleOrDefaultAsync</c>, never <c>FirstOrDefault</c>:
    /// the <c>(TenantId, ProviderMessageId)</c> index is NOT database-unique
    /// (CP2.2 mandate §26 — indexed but not <c>.IsUnique()</c>, tenant-scoped
    /// mirrors every other index on this table). Silently picking "most
    /// recent" if two rows ever matched could apply a status update to the
    /// wrong Message; throwing lets this surface loudly as a handler
    /// failure (Wolverine retry/DLQ), consistent with never swallowing an
    /// anomaly this checkpoint has no principled way to resolve on its own.
    /// </summary>
    Task<Message?> GetByProviderMessageIdAsync(string providerMessageId, CancellationToken cancellationToken);
}
