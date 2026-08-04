using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.PropertyManagement.Domain;

namespace IHostPro.Contexts.PropertyManagement.Tests.Unit.Application.Condominiums;

/// <summary>Hand-written test double — this project uses no mocking library, consistent with the rest of the solution.</summary>
internal sealed class FakeCondominiumRepository : IRepository<Condominium, Guid>
{
    private readonly Condominium? _condominium;

    private FakeCondominiumRepository(Condominium? condominium) => _condominium = condominium;

    public static FakeCondominiumRepository WithCondominium(Condominium? condominium) => new(condominium);

    public int GetByIdCallCount { get; private set; }
    public Guid? LastRequestedId { get; private set; }
    public List<Condominium> AddedCondominiums { get; } = [];

    public Task<Condominium?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        GetByIdCallCount++;
        LastRequestedId = id;
        return Task.FromResult(_condominium);
    }

    public void Add(Condominium aggregate) => AddedCondominiums.Add(aggregate);
    public void Update(Condominium aggregate) => throw new NotSupportedException("Not exercised — mutation happens via the tracked instance itself.");
    public void Remove(Condominium aggregate) => throw new NotSupportedException("No exclusion endpoint exists in this checkpoint.");
}
