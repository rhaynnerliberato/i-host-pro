using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.Housekeeping.Domain;

namespace IHostPro.Contexts.Housekeeping.Tests.Unit.Application.Cleanings;

/// <summary>Hand-written test double — this project uses no mocking library, consistent with the rest of the solution.</summary>
internal sealed class FakeCleaningRepository : IRepository<Cleaning, Guid>
{
    private readonly Cleaning? _cleaning;

    private FakeCleaningRepository(Cleaning? cleaning) => _cleaning = cleaning;

    public static FakeCleaningRepository WithCleaning(Cleaning? cleaning) => new(cleaning);

    public int GetByIdCallCount { get; private set; }
    public Guid? LastRequestedId { get; private set; }
    public List<Cleaning> AddedCleanings { get; } = [];

    public Task<Cleaning?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        GetByIdCallCount++;
        LastRequestedId = id;
        return Task.FromResult(_cleaning);
    }

    public void Add(Cleaning aggregate) => AddedCleanings.Add(aggregate);
    public void Update(Cleaning aggregate) => throw new NotSupportedException("Not exercised — mutation happens via the tracked instance itself.");
    public void Remove(Cleaning aggregate) => throw new NotSupportedException("No deletion endpoint exists in this checkpoint.");
}
