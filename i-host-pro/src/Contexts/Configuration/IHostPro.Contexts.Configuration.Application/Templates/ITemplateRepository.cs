using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.Configuration.Domain;

namespace IHostPro.Contexts.Configuration.Application.Templates;

/// <summary>
/// Application-facing repository for <see cref="Template"/> — extends the
/// generic <see cref="IRepository{TAggregate,TId}"/> with the one
/// business-key lookup every command/query here actually needs (exactly one
/// row per (TenantId, Key) — see <c>TemplateConfiguration</c>'s unique
/// index). Tenant scoping is handled transparently by
/// <c>BaseDbContext</c>'s Global Query Filter + RLS — never a
/// caller-supplied <c>tenantId</c> parameter here.
/// </summary>
public interface ITemplateRepository : IRepository<Template, Guid>
{
    Task<Template?> GetByKeyAsync(string key, CancellationToken cancellationToken);
}
