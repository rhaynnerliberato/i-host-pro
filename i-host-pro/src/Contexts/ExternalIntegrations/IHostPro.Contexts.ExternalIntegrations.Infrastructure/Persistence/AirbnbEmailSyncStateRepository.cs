using IHostPro.Contexts.ExternalIntegrations.Application.AirbnbEmailBridge;
using IHostPro.Contexts.ExternalIntegrations.Domain;
using Microsoft.EntityFrameworkCore;

namespace IHostPro.Contexts.ExternalIntegrations.Infrastructure.Persistence;

public sealed class AirbnbEmailSyncStateRepository : IAirbnbEmailSyncStateRepository
{
    private readonly ExternalIntegrationsDbContext _dbContext;

    public AirbnbEmailSyncStateRepository(ExternalIntegrationsDbContext dbContext) => _dbContext = dbContext;

    public Task<AirbnbEmailSyncState?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        _dbContext.AirbnbEmailSyncStates.FirstOrDefaultAsync(s => s.Id == id, cancellationToken);

    public Task<AirbnbEmailSyncState?> GetForCurrentTenantAsync(Guid mailboxConnectionId, string mailFolderId, CancellationToken cancellationToken) =>
        _dbContext.AirbnbEmailSyncStates.FirstOrDefaultAsync(
            s => s.MailboxConnectionId == mailboxConnectionId && s.MailFolderId == mailFolderId, cancellationToken);

    public void Add(AirbnbEmailSyncState aggregate) => _dbContext.AirbnbEmailSyncStates.Add(aggregate);

    public void Update(AirbnbEmailSyncState aggregate) => _dbContext.AirbnbEmailSyncStates.Update(aggregate);

    public void Remove(AirbnbEmailSyncState aggregate) => _dbContext.AirbnbEmailSyncStates.Remove(aggregate);
}
