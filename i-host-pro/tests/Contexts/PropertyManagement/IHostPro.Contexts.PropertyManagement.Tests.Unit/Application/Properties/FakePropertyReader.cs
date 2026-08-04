using IHostPro.BuildingBlocks.Application;
using IHostPro.Contexts.PropertyManagement.Application.Properties;

namespace IHostPro.Contexts.PropertyManagement.Tests.Unit.Application.Properties;

/// <summary>Hand-written test double — this project uses no mocking library, consistent with the rest of the solution.</summary>
internal sealed class FakePropertyReader : IPropertyReader
{
    private readonly PagedResult<PropertySummaryResult> _listResult;
    private readonly PropertyResult? _detailResult;

    private FakePropertyReader(PagedResult<PropertySummaryResult> listResult, PropertyResult? detailResult)
    {
        _listResult = listResult;
        _detailResult = detailResult;
    }

    public static FakePropertyReader WithList(PagedResult<PropertySummaryResult> listResult) =>
        new(listResult, detailResult: null);

    public static FakePropertyReader WithDetail(PropertyResult? detailResult) =>
        new(new PagedResult<PropertySummaryResult>(1, 20, 0, []), detailResult);

    public int? LastRequestedPage { get; private set; }
    public int? LastRequestedPageSize { get; private set; }
    public Guid? LastRequestedId { get; private set; }
    public Guid? LastRequestedOwnerUserId { get; private set; }

    public Task<PagedResult<PropertySummaryResult>> ListAsync(int page, int pageSize, CancellationToken cancellationToken)
    {
        LastRequestedPage = page;
        LastRequestedPageSize = pageSize;
        return Task.FromResult(_listResult);
    }

    public Task<PropertyResult?> GetByIdAsync(Guid propertyId, CancellationToken cancellationToken)
    {
        LastRequestedId = propertyId;
        return Task.FromResult(_detailResult);
    }

    public Task<PagedResult<PropertySummaryResult>> ListMineAsync(Guid ownerUserId, int page, int pageSize, CancellationToken cancellationToken)
    {
        LastRequestedOwnerUserId = ownerUserId;
        LastRequestedPage = page;
        LastRequestedPageSize = pageSize;
        return Task.FromResult(_listResult);
    }

    public Task<PropertyResult?> GetMineDetailAsync(Guid ownerUserId, Guid propertyId, CancellationToken cancellationToken)
    {
        LastRequestedOwnerUserId = ownerUserId;
        LastRequestedId = propertyId;
        return Task.FromResult(_detailResult);
    }
}
