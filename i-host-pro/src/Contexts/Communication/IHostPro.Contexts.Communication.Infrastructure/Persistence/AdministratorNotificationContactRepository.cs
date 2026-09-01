using IHostPro.Contexts.Communication.Application;
using IHostPro.Contexts.Communication.Domain;
using Microsoft.EntityFrameworkCore;

namespace IHostPro.Contexts.Communication.Infrastructure.Persistence;

public sealed class AdministratorNotificationContactRepository : IAdministratorNotificationContactRepository
{
    private readonly CommunicationDbContext _dbContext;

    public AdministratorNotificationContactRepository(CommunicationDbContext dbContext) => _dbContext = dbContext;

    public void Add(AdministratorNotificationContact contact) => _dbContext.AdministratorNotificationContacts.Add(contact);

    public void Update(AdministratorNotificationContact contact) => _dbContext.AdministratorNotificationContacts.Update(contact);

    public Task<AdministratorNotificationContact?> GetActiveByTenantIdAsync(Guid tenantId, CancellationToken cancellationToken) =>
        _dbContext.AdministratorNotificationContacts.FirstOrDefaultAsync(
            c => c.TenantId == tenantId && c.IsActive, cancellationToken);
}
