using IHostPro.BuildingBlocks.Application;
using IHostPro.Contexts.PropertyManagement.Application.Owners;
using IHostPro.Contexts.PropertyManagement.Domain;
using Microsoft.EntityFrameworkCore;

namespace IHostPro.Contexts.PropertyManagement.Infrastructure.Persistence;

/// <inheritdoc cref="IPropertyOwnerReader"/>
/// <remarks>
/// Reads exclusively from <see cref="PropertyManagementDbContext"/> —
/// <c>PropertyOwnerLinks</c> is <c>ITenantOwned</c>, so the DbContext's
/// Global Query Filter already scopes every query here to the current
/// tenant. <see cref="ListByPropertyAsync"/> never queries Identity — the
/// projected <see cref="PropertyOwnerResult"/> carries only what
/// <c>property_owners</c> itself stores.
/// </remarks>
public sealed class PropertyOwnerReader : IPropertyOwnerReader
{
    public const int MaxPageSize = 100;

    private readonly PropertyManagementDbContext _dbContext;

    public PropertyOwnerReader(PropertyManagementDbContext dbContext) => _dbContext = dbContext;

    public async Task<bool> ExistsAsync(Guid propertyId, Guid ownerUserId, CancellationToken cancellationToken) =>
        await _dbContext.PropertyOwnerLinks
            .AsNoTracking()
            .AnyAsync(l => l.PropertyId == propertyId && l.OwnerUserId == ownerUserId, cancellationToken);

    public async Task<PropertyOwnerLink?> FindAsync(Guid propertyId, Guid ownerUserId, CancellationToken cancellationToken) =>
        await _dbContext.PropertyOwnerLinks
            .FirstOrDefaultAsync(l => l.PropertyId == propertyId && l.OwnerUserId == ownerUserId, cancellationToken);

    public async Task<PagedResult<PropertyOwnerResult>> ListByPropertyAsync(
        Guid propertyId, int page, int pageSize, CancellationToken cancellationToken)
    {
        var effectivePage = Math.Max(page, 1);
        var effectivePageSize = Math.Clamp(pageSize, 1, MaxPageSize);

        var query = _dbContext.PropertyOwnerLinks.AsNoTracking().Where(l => l.PropertyId == propertyId);

        var totalCount = await query.CountAsync(cancellationToken);

        var items = await query
            .OrderBy(l => l.CreatedAt)
            .ThenBy(l => l.OwnerUserId)
            .Skip((effectivePage - 1) * effectivePageSize)
            .Take(effectivePageSize)
            .Select(l => new PropertyOwnerResult(l.PropertyId, l.OwnerUserId, l.CreatedAt))
            .ToListAsync(cancellationToken);

        return new PagedResult<PropertyOwnerResult>(effectivePage, effectivePageSize, totalCount, items);
    }
}
