using IHostPro.BuildingBlocks.Application;
using IHostPro.Contexts.PropertyManagement.Application.Condominiums;
using IHostPro.Contexts.PropertyManagement.Application.Properties;
using Microsoft.EntityFrameworkCore;

namespace IHostPro.Contexts.PropertyManagement.Infrastructure.Persistence;

/// <inheritdoc cref="IPropertyReader"/>
/// <remarks>
/// Reads exclusively from <see cref="PropertyManagementDbContext"/> —
/// <c>Properties</c> is <c>ITenantOwned</c>, so the DbContext's Global Query
/// Filter already scopes every query here to the current tenant (mirrors
/// <c>CondominiumReader</c>). <see cref="ListAsync"/> projects only the
/// summary columns — <c>Address</c>'s owned columns are never selected at
/// all (Checkpoint 3 plan, item 9: "listagem nunca retorna endereço próprio
/// ou efetivo"), and the whole page is materialized in ONE query, no
/// per-row follow-up (Checkpoint 3 plan, item 9: "evitar N+1").
///
/// Result mapping happens client-side, after <c>ToListAsync</c>/
/// <c>FirstOrDefaultAsync</c> — mirrors <c>UserAdministrationReader</c>'s own
/// convention exactly, since <see cref="Domain.ValueObjects.PropertyCode"/>'s
/// converted <c>Value</c> member cannot be translated inside a server-side
/// LINQ projection.
/// </remarks>
public sealed class PropertyReader : IPropertyReader
{
    public const int MaxPageSize = 100;

    private readonly PropertyManagementDbContext _dbContext;

    public PropertyReader(PropertyManagementDbContext dbContext) => _dbContext = dbContext;

    public async Task<PagedResult<PropertySummaryResult>> ListAsync(
        int page, int pageSize, CancellationToken cancellationToken)
    {
        var effectivePage = Math.Max(page, 1);
        var effectivePageSize = Math.Clamp(pageSize, 1, MaxPageSize);

        var query = _dbContext.Properties.AsNoTracking();

        var totalCount = await query.CountAsync(cancellationToken);

        var properties = await query
            .OrderBy(p => p.NormalizedCode)
            .ThenBy(p => p.Id)
            .Skip((effectivePage - 1) * effectivePageSize)
            .Take(effectivePageSize)
            .ToListAsync(cancellationToken);

        var items = properties
            .Select(p => new PropertySummaryResult(
                p.Id,
                p.Code.Value,
                p.Name,
                p.Capacity,
                p.CondominiumId,
                PropertyStatusCodeMapper.ToCode(p.Status),
                p.CreatedAt,
                p.UpdatedAt))
            .ToArray();

        return new PagedResult<PropertySummaryResult>(effectivePage, effectivePageSize, totalCount, items);
    }

    /// <remarks>
    /// Two sequential queries, not a single join — deliberate, and not an
    /// N+1 concern: this resolves a SINGLE property's effective address, the
    /// same shape of work <see cref="CreatePropertyCommandHandler"/>/
    /// <see cref="UpdatePropertyCommandHandler"/> already do via
    /// <see cref="ICondominiumReader.GetAddressByIdAsync"/> for the very same
    /// reason. The second query only runs when the property has no own
    /// address (Checkpoint 3 plan, item 9).
    /// </remarks>
    public async Task<PropertyResult?> GetByIdAsync(Guid propertyId, CancellationToken cancellationToken)
    {
        var property = await _dbContext.Properties
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == propertyId, cancellationToken);

        if (property is null)
            return null;

        var ownAddress = property.Address is not null ? AddressResultMapper.ToResult(property.Address) : null;

        AddressResult effectiveAddress;
        string effectiveAddressSource;

        if (ownAddress is not null)
        {
            effectiveAddress = ownAddress;
            effectiveAddressSource = "property";
        }
        else
        {
            // property.CondominiumId is guaranteed non-null here by the
            // ck_properties_effective_address_source CHECK constraint.
            var condominium = await _dbContext.Condominiums
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == property.CondominiumId!.Value, cancellationToken);

            effectiveAddress = AddressResultMapper.ToResult(condominium!.Address);
            effectiveAddressSource = "condominium";
        }

        return new PropertyResult(
            property.Id,
            property.Code.Value,
            property.Name,
            property.Capacity,
            property.CondominiumId,
            ownAddress,
            effectiveAddress,
            effectiveAddressSource,
            PropertyStatusCodeMapper.ToCode(property.Status),
            property.CreatedAt,
            property.UpdatedAt);
    }

    /// <remarks>
    /// A single query — the owner filter is expressed as a subquery against
    /// <c>property_owners</c> (both tables share the same Global Query
    /// Filter tenant scoping), never a per-row follow-up (Checkpoint 5 plan,
    /// item 10: "sem N+1").
    /// </remarks>
    public async Task<PagedResult<PropertySummaryResult>> ListMineAsync(
        Guid ownerUserId, int page, int pageSize, CancellationToken cancellationToken)
    {
        var effectivePage = Math.Max(page, 1);
        var effectivePageSize = Math.Clamp(pageSize, 1, MaxPageSize);

        var ownedPropertyIds = _dbContext.PropertyOwnerLinks
            .Where(l => l.OwnerUserId == ownerUserId)
            .Select(l => l.PropertyId);

        var query = _dbContext.Properties.AsNoTracking().Where(p => ownedPropertyIds.Contains(p.Id));

        var totalCount = await query.CountAsync(cancellationToken);

        var properties = await query
            .OrderBy(p => p.NormalizedCode)
            .ThenBy(p => p.Id)
            .Skip((effectivePage - 1) * effectivePageSize)
            .Take(effectivePageSize)
            .ToListAsync(cancellationToken);

        var items = properties
            .Select(p => new PropertySummaryResult(
                p.Id,
                p.Code.Value,
                p.Name,
                p.Capacity,
                p.CondominiumId,
                PropertyStatusCodeMapper.ToCode(p.Status),
                p.CreatedAt,
                p.UpdatedAt))
            .ToArray();

        return new PagedResult<PropertySummaryResult>(effectivePage, effectivePageSize, totalCount, items);
    }

    /// <remarks>
    /// Two queries — the ownership check, then the detail (reusing
    /// <see cref="GetByIdAsync"/>'s own effective-address resolution rather
    /// than duplicating it) — not an N+1 concern for a single detail item,
    /// same reasoning as <see cref="GetByIdAsync"/>'s own two-query shape
    /// (Checkpoint 5 plan, item 10).
    /// </remarks>
    public async Task<PropertyResult?> GetMineDetailAsync(Guid ownerUserId, Guid propertyId, CancellationToken cancellationToken)
    {
        var isLinkedToOwner = await _dbContext.PropertyOwnerLinks
            .AsNoTracking()
            .AnyAsync(l => l.PropertyId == propertyId && l.OwnerUserId == ownerUserId, cancellationToken);

        if (!isLinkedToOwner)
            return null;

        return await GetByIdAsync(propertyId, cancellationToken);
    }
}
