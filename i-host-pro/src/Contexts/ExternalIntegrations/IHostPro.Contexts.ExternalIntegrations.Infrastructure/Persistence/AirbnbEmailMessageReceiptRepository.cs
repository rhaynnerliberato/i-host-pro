using IHostPro.BuildingBlocks.Application;
using IHostPro.Contexts.ExternalIntegrations.Application.AirbnbEmailBridge;
using IHostPro.Contexts.ExternalIntegrations.Domain;
using Microsoft.EntityFrameworkCore;

namespace IHostPro.Contexts.ExternalIntegrations.Infrastructure.Persistence;

public sealed class AirbnbEmailMessageReceiptRepository : IAirbnbEmailMessageReceiptRepository
{
    public const int MaxPageSize = 100;

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

    public async Task<PagedResult<AirbnbEmailMessageReceiptResult>> ListForCurrentTenantAsync(
        string? status, string? reasonCode, int page, int pageSize, CancellationToken cancellationToken)
    {
        var effectivePage = Math.Max(page, 1);
        var effectivePageSize = Math.Clamp(pageSize, 1, MaxPageSize);

        var query = _dbContext.AirbnbEmailMessageReceipts.AsNoTracking();

        if (status is not null)
        {
            if (!Enum.TryParse<AirbnbEmailMessageProcessingStatus>(status, ignoreCase: true, out var statusEnum))
                return new PagedResult<AirbnbEmailMessageReceiptResult>(effectivePage, effectivePageSize, 0, []);

            query = query.Where(r => r.ProcessingStatus == statusEnum);
        }

        if (reasonCode is not null)
            query = query.Where(r => r.FailureReason == reasonCode);

        var totalCount = await query.CountAsync(cancellationToken);

        var receipts = await query
            .OrderByDescending(r => r.CreatedAtUtc)
            .ThenByDescending(r => r.Id)
            .Skip((effectivePage - 1) * effectivePageSize)
            .Take(effectivePageSize)
            .ToListAsync(cancellationToken);

        var items = receipts.Select(ToResult).ToArray();

        return new PagedResult<AirbnbEmailMessageReceiptResult>(effectivePage, effectivePageSize, totalCount, items);
    }

    private static AirbnbEmailMessageReceiptResult ToResult(AirbnbEmailMessageReceipt receipt) => new(
        receipt.Id,
        receipt.ReceivedAtUtc,
        receipt.ProcessingStatus.ToString(),
        receipt.DetectedEventType,
        receipt.ParserVersion,
        receipt.FailureReason,
        receipt.UnmatchedListingTitle,
        receipt.ProcessedAtUtc,
        receipt.CreatedAtUtc);

    public void Add(AirbnbEmailMessageReceipt aggregate) => _dbContext.AirbnbEmailMessageReceipts.Add(aggregate);

    public void Update(AirbnbEmailMessageReceipt aggregate) => _dbContext.AirbnbEmailMessageReceipts.Update(aggregate);

    public void Remove(AirbnbEmailMessageReceipt aggregate) => _dbContext.AirbnbEmailMessageReceipts.Remove(aggregate);
}
