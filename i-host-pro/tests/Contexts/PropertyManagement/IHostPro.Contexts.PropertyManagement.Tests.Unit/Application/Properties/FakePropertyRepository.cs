using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.PropertyManagement.Domain;

namespace IHostPro.Contexts.PropertyManagement.Tests.Unit.Application.Properties;

/// <summary>Hand-written test double — this project uses no mocking library, consistent with the rest of the solution.</summary>
internal sealed class FakePropertyRepository : IRepository<Property, Guid>
{
    private readonly Property? _property;

    private FakePropertyRepository(Property? property) => _property = property;

    public static FakePropertyRepository WithProperty(Property? property) => new(property);

    public int GetByIdCallCount { get; private set; }
    public Guid? LastRequestedId { get; private set; }
    public List<Property> AddedProperties { get; } = [];

    public Task<Property?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        GetByIdCallCount++;
        LastRequestedId = id;
        return Task.FromResult(_property);
    }

    public void Add(Property aggregate) => AddedProperties.Add(aggregate);
    public void Update(Property aggregate) => throw new NotSupportedException("Not exercised — mutation happens via the tracked instance itself.");
    public void Remove(Property aggregate) => throw new NotSupportedException("No exclusion endpoint exists in this checkpoint.");
}
