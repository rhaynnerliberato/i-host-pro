using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.BuildingBlocks.Infrastructure.Persistence;
using IHostPro.Contexts.Configuration.Contracts;
using IHostPro.Contexts.Configuration.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IHostPro.Contexts.Configuration.Infrastructure.Templates;

/// <inheritdoc cref="ITemplateReader"/>
/// <remarks>
/// The only implementation permitted to exist for <see cref="ITemplateReader"/>
/// — lives in <c>Configuration.Infrastructure</c>, the one layer allowed to
/// touch <see cref="ConfigurationDbContext"/> directly. Opens its own
/// short-lived, read-only, tenant-scoped transaction via
/// <see cref="TenantAwareTransactionScope"/> — mirrors
/// <c>PropertyReservationEligibilityReader"/>/<c>ReservationGuestContactReader</c>'s
/// own reasoning exactly. No cache — Templates are not hierarchically
/// resolved like Policy values, a plain indexed lookup is proportional for
/// this checkpoint (Fase 9, Checkpoint 1).
/// </remarks>
public sealed class TemplateReader : ITemplateReader
{
    private readonly ConfigurationDbContext _dbContext;

    public TemplateReader(ConfigurationDbContext dbContext) => _dbContext = dbContext;

    public async Task<ActiveTemplate?> GetActiveByKeyAsync(Guid tenantId, string key, CancellationToken cancellationToken)
    {
        var scopeTenantContext = new TenantContext();
        scopeTenantContext.SetTenant(tenantId);

        await using var transaction = await TenantAwareTransactionScope.BeginAsync(
            _dbContext, scopeTenantContext, readOnly: true, cancellationToken);

        var template = await _dbContext.Templates
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Key == key && t.IsActive, cancellationToken);

        return template is null ? null : new ActiveTemplate(template.Key, template.Content);
    }
}
