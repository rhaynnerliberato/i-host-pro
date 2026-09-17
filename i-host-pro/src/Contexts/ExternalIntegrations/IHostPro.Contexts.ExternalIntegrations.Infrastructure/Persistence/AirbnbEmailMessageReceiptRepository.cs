using IHostPro.Contexts.ExternalIntegrations.Application.AirbnbEmailBridge;
using IHostPro.Contexts.ExternalIntegrations.Domain;
using Microsoft.EntityFrameworkCore;

namespace IHostPro.Contexts.ExternalIntegrations.Infrastructure.Persistence;

public sealed class AirbnbEmailMessageReceiptRepository : IAirbnbEmailMessageReceiptRepository
{
    private readonly ExternalIntegrationsDbContext _dbContext;

    public AirbnbEmailMessageReceiptRepository(ExternalIntegrationsDbContext dbContext) => _dbContext = dbContext;

    public Task<AirbnbEmailMessageReceipt?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        _dbContext.AirbnbEmailMessageReceipts.FirstOrDefaultAsync(r => r.Id == id, cancellationToken);

    public Task<bool> ExistsForCurrentTenantAsync(string graphMessageId, CancellationToken cancellationToken) =>
        _dbContext.AirbnbEmailMessageReceipts.AnyAsync(r => r.GraphMessageId == graphMessageId, cancellationToken);

    public async Task<IReadOnlyDictionary<AirbnbEmailMessageProcessingStatus, int>> CountByProcessingStatusForCurrentTenantAsync(
        CancellationToken cancellationToken)
    {
        var counts = await _dbContext.AirbnbEmailMessageReceipts
            .GroupBy(r => r.ProcessingStatus)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        return counts.ToDictionary(x => x.Status, x => x.Count);
    }

    public void Add(AirbnbEmailMessageReceipt aggregate) => _dbContext.AirbnbEmailMessageReceipts.Add(aggregate);

    public void Update(AirbnbEmailMessageReceipt aggregate) => _dbContext.AirbnbEmailMessageReceipts.Update(aggregate);

    public void Remove(AirbnbEmailMessageReceipt aggregate) => _dbContext.AirbnbEmailMessageReceipts.Remove(aggregate);
}
