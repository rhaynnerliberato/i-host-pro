using IHostPro.BuildingBlocks.Application;
using IHostPro.Contexts.PropertyManagement.Application.Owners;
using IHostPro.Contexts.PropertyManagement.Domain;

namespace IHostPro.Contexts.PropertyManagement.Tests.Unit.Application.Owners;

/// <summary>Hand-written test double — this project uses no mocking library, consistent with the rest of the solution.</summary>
internal sealed class FakePropertyOwnerReader : IPropertyOwnerReader
{
    private readonly bool _exists;
    private readonly PropertyOwnerLink? _findResult;
    private readonly PagedResult<PropertyOwnerResult> _listResult;

    private FakePropertyOwnerReader(bool exists, PropertyOwnerLink? findResult, PagedResult<PropertyOwnerResult> listResult)
    {
        _exists = exists;
        _findResult = findResult;
        _listResult = listResult;
    }

    public static FakePropertyOwnerReader WithExists(bool exists) =>
        new(exists, findResult: null, new PagedResult<PropertyOwnerResult>(1, 20, 0, []));

    public static FakePropertyOwnerReader WithFindResult(PropertyOwnerLink? findResult) =>
        new(exists: false, findResult, new PagedResult<PropertyOwnerResult>(1, 20, 0, []));

    public static FakePropertyOwnerReader WithListResult(PagedResult<PropertyOwnerResult> listResult) =>
        new(exists: false, findResult: null, listResult);

    public Guid? LastRequestedPropertyId { get; private set; }
    public Guid? LastRequestedOwnerUserId { get; private set; }
    public int? LastRequestedPage { get; private set; }
    public int? LastRequestedPageSize { get; private set; }

    public Task<bool> ExistsAsync(Guid propertyId, Guid ownerUserId, CancellationToken cancellationToken)
    {
        LastRequestedPropertyId = propertyId;
        LastRequestedOwnerUserId = ownerUserId;
        return Task.FromResult(_exists);
    }

    public Task<PropertyOwnerLink?> FindAsync(Guid propertyId, Guid ownerUserId, CancellationToken cancellationToken)
    {
        LastRequestedPropertyId = propertyId;
        LastRequestedOwnerUserId = ownerUserId;
        return Task.FromResult(_findResult);
    }

    public Task<PagedResult<PropertyOwnerResult>> ListByPropertyAsync(
        Guid propertyId, int page, int pageSize, CancellationToken cancellationToken)
    {
        LastRequestedPropertyId = propertyId;
        LastRequestedPage = page;
        LastRequestedPageSize = pageSize;
        return Task.FromResult(_listResult);
    }
}
