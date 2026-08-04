using IHostPro.BuildingBlocks.Application;
using IHostPro.Contexts.PropertyManagement.Application.Condominiums;
using Microsoft.EntityFrameworkCore;

namespace IHostPro.Contexts.PropertyManagement.Infrastructure.Persistence;

/// <inheritdoc cref="ICondominiumReader"/>
/// <remarks>
/// Reads exclusively from <see cref="PropertyManagementDbContext"/> —
/// <c>Condominiums</c> is <c>ITenantOwned</c>, so the DbContext's Global
/// Query Filter already scopes every query here to the current tenant
/// (mirrors <c>UserAdministrationReader</c>). <see cref="ListAsync"/>
/// projects only <c>Id</c>/<c>Name</c>/<c>CreatedAt</c>/<c>UpdatedAt</c> —
/// <c>Address</c>'s owned columns are never selected at all, not merely
/// dropped after loading (Checkpoint 2 plan, item 4: "não retornar endereço
/// completo").
/// </remarks>
public sealed class CondominiumReader : ICondominiumReader
{
    public const int MaxPageSize = 100;

    private readonly PropertyManagementDbContext _dbContext;

    public CondominiumReader(PropertyManagementDbContext dbContext) => _dbContext = dbContext;

    public async Task<PagedResult<CondominiumSummaryResult>> ListAsync(
        int page, int pageSize, CancellationToken cancellationToken)
    {
        var effectivePage = Math.Max(page, 1);
        var effectivePageSize = Math.Clamp(pageSize, 1, MaxPageSize);

        var query = _dbContext.Condominiums.AsNoTracking();

        var totalCount = await query.CountAsync(cancellationToken);

        var items = await query
            .OrderBy(c => c.Name)
            .ThenBy(c => c.Id)
            .Skip((effectivePage - 1) * effectivePageSize)
            .Take(effectivePageSize)
            .Select(c => new CondominiumSummaryResult(c.Id, c.Name, c.CreatedAt, c.UpdatedAt))
            .ToListAsync(cancellationToken);

        return new PagedResult<CondominiumSummaryResult>(effectivePage, effectivePageSize, totalCount, items);
    }

    public async Task<CondominiumResult?> GetByIdAsync(Guid condominiumId, CancellationToken cancellationToken)
    {
        var condominium = await _dbContext.Condominiums
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == condominiumId, cancellationToken);

        if (condominium is null)
            return null;

        return new CondominiumResult(
            condominium.Id,
            condominium.Name,
            AddressResultMapper.ToResult(condominium.Address),
            condominium.CreatedAt,
            condominium.UpdatedAt);
    }

    public async Task<AddressResult?> GetAddressByIdAsync(Guid condominiumId, CancellationToken cancellationToken)
    {
        var condominium = await _dbContext.Condominiums
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == condominiumId, cancellationToken);

        return condominium is null ? null : AddressResultMapper.ToResult(condominium.Address);
    }
}
