using IHostPro.BuildingBlocks.Application;
using IHostPro.Contexts.PropertyManagement.Application.Condominiums;

namespace IHostPro.Contexts.PropertyManagement.Tests.Unit.Application.Condominiums;

/// <summary>
/// Hand-written test double — this project uses no mocking library,
/// consistent with the rest of the solution. Reused by Property unit tests
/// (Checkpoint 3) for <see cref="GetAddressByIdAsync"/>, the same way
/// Property's Create/Update handlers reuse <see cref="ICondominiumReader"/>
/// itself.
/// </summary>
internal sealed class FakeCondominiumReader : ICondominiumReader
{
    private readonly PagedResult<CondominiumSummaryResult> _listResult;
    private readonly CondominiumResult? _detailResult;
    private readonly AddressResult? _addressResult;

    private FakeCondominiumReader(
        PagedResult<CondominiumSummaryResult> listResult, CondominiumResult? detailResult, AddressResult? addressResult)
    {
        _listResult = listResult;
        _detailResult = detailResult;
        _addressResult = addressResult;
    }

    public static FakeCondominiumReader WithList(PagedResult<CondominiumSummaryResult> listResult) =>
        new(listResult, detailResult: null, addressResult: null);

    public static FakeCondominiumReader WithDetail(CondominiumResult? detailResult) =>
        new(new PagedResult<CondominiumSummaryResult>(1, 20, 0, []), detailResult, addressResult: null);

    public static FakeCondominiumReader WithAddress(AddressResult? addressResult) =>
        new(new PagedResult<CondominiumSummaryResult>(1, 20, 0, []), detailResult: null, addressResult);

    public int? LastRequestedPage { get; private set; }
    public int? LastRequestedPageSize { get; private set; }
    public Guid? LastRequestedId { get; private set; }
    public Guid? LastRequestedAddressId { get; private set; }

    public Task<PagedResult<CondominiumSummaryResult>> ListAsync(int page, int pageSize, CancellationToken cancellationToken)
    {
        LastRequestedPage = page;
        LastRequestedPageSize = pageSize;
        return Task.FromResult(_listResult);
    }

    public Task<CondominiumResult?> GetByIdAsync(Guid condominiumId, CancellationToken cancellationToken)
    {
        LastRequestedId = condominiumId;
        return Task.FromResult(_detailResult);
    }

    public Task<AddressResult?> GetAddressByIdAsync(Guid condominiumId, CancellationToken cancellationToken)
    {
        LastRequestedAddressId = condominiumId;
        return Task.FromResult(_addressResult);
    }
}
