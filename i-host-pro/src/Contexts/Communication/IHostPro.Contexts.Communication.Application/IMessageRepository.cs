using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.Communication.Domain;

namespace IHostPro.Contexts.Communication.Application;

public interface IMessageRepository : IRepository<Message, Guid>
{
    /// <summary>The single idempotency check this checkpoint needs (CP1 mandate §35) — a DB unique constraint on <c>IdempotencyKey</c> backstops this, mirrors ADR-018's own two-layer idempotency precedent.</summary>
    Task<Message?> GetByIdempotencyKeyAsync(string idempotencyKey, CancellationToken cancellationToken);
}
