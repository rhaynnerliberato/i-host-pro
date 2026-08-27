using IHostPro.Contexts.PropertyManagement.Application.FrontDesk;
using IHostPro.Contexts.PropertyManagement.Domain;

namespace IHostPro.Contexts.PropertyManagement.Tests.Unit.Application.FrontDesk;

/// <summary>Hand-written test double — this project uses no mocking library, consistent with the rest of the solution.</summary>
internal sealed class FakeFrontDeskContactRepository : IFrontDeskContactRepository
{
    private readonly FrontDeskContact? _existing;

    private FakeFrontDeskContactRepository(FrontDeskContact? existing) => _existing = existing;

    public static FakeFrontDeskContactRepository WithExisting(FrontDeskContact? existing) => new(existing);

    public List<FrontDeskContact> AddedContacts { get; } = [];
    public List<FrontDeskContact> UpdatedContacts { get; } = [];

    public Task<FrontDeskContact?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_existing?.Id == id ? _existing : null);

    public Task<FrontDeskContact?> GetByCondominiumIdAsync(Guid condominiumId, CancellationToken cancellationToken) =>
        Task.FromResult(_existing?.CondominiumId == condominiumId ? _existing : null);

    public void Add(FrontDeskContact aggregate) => AddedContacts.Add(aggregate);
    public void Update(FrontDeskContact aggregate) => UpdatedContacts.Add(aggregate);
    public void Remove(FrontDeskContact aggregate) => throw new NotSupportedException("No delete endpoint exists — disabling is done via IsActive.");
}
