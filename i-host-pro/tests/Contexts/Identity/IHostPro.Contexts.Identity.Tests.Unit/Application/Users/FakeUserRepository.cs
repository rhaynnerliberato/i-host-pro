using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.Identity.Domain;

namespace IHostPro.Contexts.Identity.Tests.Unit.Application.Users;

/// <summary>Hand-written test double — this project uses no mocking library, consistent with the rest of the solution.</summary>
internal sealed class FakeUserRepository : IRepository<User, Guid>
{
    private readonly User? _user;

    private FakeUserRepository(User? user) => _user = user;

    public static FakeUserRepository WithUser(User? user) => new(user);

    public int GetByIdCallCount { get; private set; }
    public Guid? LastRequestedId { get; private set; }

    public Task<User?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        GetByIdCallCount++;
        LastRequestedId = id;
        return Task.FromResult(_user);
    }

    public void Add(User aggregate) => throw new NotSupportedException("Not exercised by AssignRole/RemoveRole handler tests.");
    public void Update(User aggregate) => throw new NotSupportedException("Not exercised by AssignRole/RemoveRole handler tests.");
    public void Remove(User aggregate) => throw new NotSupportedException("Not exercised by AssignRole/RemoveRole handler tests.");
}
