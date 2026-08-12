using IHostPro.BuildingBlocks.Application;
using IHostPro.Contexts.Housekeeping.Application.Cleanings;

namespace IHostPro.Contexts.Housekeeping.Tests.Unit.Application.Cleanings;

internal sealed class FakeCleaningReader : ICleaningReader
{
    private readonly CleaningResult? _detail;
    private readonly IReadOnlyList<CleaningSummaryResult> _summaries;

    private FakeCleaningReader(CleaningResult? detail, IReadOnlyList<CleaningSummaryResult> summaries)
    {
        _detail = detail;
        _summaries = summaries;
    }

    public static FakeCleaningReader WithDetail(CleaningResult? detail) => new(detail, []);

    public static FakeCleaningReader WithSummaries(IReadOnlyList<CleaningSummaryResult> summaries) => new(null, summaries);

    public string? LastStatus { get; private set; }
    public Guid? LastPropertyId { get; private set; }
    public Guid? LastAssignedHousekeeperUserId { get; private set; }
    public int? LastPage { get; private set; }
    public int? LastPageSize { get; private set; }
    public Guid? LastHousekeeperUserId { get; private set; }
    public Guid? LastCleaningId { get; private set; }

    public Task<PagedResult<CleaningSummaryResult>> ListAsync(
        string? status, Guid? propertyId, Guid? assignedHousekeeperUserId,
        int page, int pageSize, CancellationToken cancellationToken)
    {
        LastStatus = status;
        LastPropertyId = propertyId;
        LastAssignedHousekeeperUserId = assignedHousekeeperUserId;
        LastPage = page;
        LastPageSize = pageSize;

        return Task.FromResult(new PagedResult<CleaningSummaryResult>(page, pageSize, _summaries.Count, _summaries));
    }

    public Task<CleaningResult?> GetByIdAsync(Guid cleaningId, CancellationToken cancellationToken) =>
        Task.FromResult(_detail);

    public Task<PagedResult<CleaningSummaryResult>> ListForHousekeeperAsync(
        Guid housekeeperUserId, string? status, int page, int pageSize, CancellationToken cancellationToken)
    {
        LastHousekeeperUserId = housekeeperUserId;
        LastStatus = status;
        LastPage = page;
        LastPageSize = pageSize;

        return Task.FromResult(new PagedResult<CleaningSummaryResult>(page, pageSize, _summaries.Count, _summaries));
    }

    public Task<CleaningResult?> GetByIdForHousekeeperAsync(
        Guid cleaningId, Guid housekeeperUserId, CancellationToken cancellationToken)
    {
        LastCleaningId = cleaningId;
        LastHousekeeperUserId = housekeeperUserId;

        return Task.FromResult(_detail is not null && _detail.AssignedHousekeeperUserId == housekeeperUserId ? _detail : null);
    }
}
