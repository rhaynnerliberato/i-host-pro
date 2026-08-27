using IHostPro.Contexts.PropertyManagement.Application.FrontDesk;
using IHostPro.Contexts.PropertyManagement.Domain;
using Microsoft.EntityFrameworkCore;

namespace IHostPro.Contexts.PropertyManagement.Infrastructure.Persistence;

/// <inheritdoc cref="IFrontDeskContactRepository"/>
/// <remarks>Mirrors <c>CondominiumRepository</c> exactly — no explicit tenant filter needed, the PropertyManagementDbContext's Global Query Filter already scopes every query.</remarks>
public sealed class FrontDeskContactRepository : IFrontDeskContactRepository
{
    private readonly PropertyManagementDbContext _dbContext;

    public FrontDeskContactRepository(PropertyManagementDbContext dbContext) => _dbContext = dbContext;

    public async Task<FrontDeskContact?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        await _dbContext.FrontDeskContacts.FirstOrDefaultAsync(c => c.Id == id, cancellationToken);

    public async Task<FrontDeskContact?> GetByCondominiumIdAsync(Guid condominiumId, CancellationToken cancellationToken) =>
        await _dbContext.FrontDeskContacts.FirstOrDefaultAsync(c => c.CondominiumId == condominiumId, cancellationToken);

    public void Add(FrontDeskContact aggregate) => _dbContext.FrontDeskContacts.Add(aggregate);

    public void Update(FrontDeskContact aggregate) => _dbContext.FrontDeskContacts.Update(aggregate);

    public void Remove(FrontDeskContact aggregate) => _dbContext.FrontDeskContacts.Remove(aggregate);
}
