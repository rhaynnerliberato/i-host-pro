using IHostPro.Contexts.Identity.Application;

namespace IHostPro.Contexts.Identity.Tests.Unit.Application.Users;

/// <summary>Hand-written test double — this project uses no mocking library, consistent with the rest of the solution.</summary>
internal sealed class FakeLastAdministratorGuard : ILastAdministratorGuard
{
    private readonly bool _anotherActiveAdministratorRemains;

    private FakeLastAdministratorGuard(bool anotherActiveAdministratorRemains) =>
        _anotherActiveAdministratorRemains = anotherActiveAdministratorRemains;

    public static FakeLastAdministratorGuard ThatAllows() => new(anotherActiveAdministratorRemains: true);

    public static FakeLastAdministratorGuard ThatRejects() => new(anotherActiveAdministratorRemains: false);

    public int CallCount { get; private set; }
    public Guid? LastTenantId { get; private set; }
    public Guid? LastUserId { get; private set; }

    public Task<bool> AnotherActiveAdministratorRemainsAsync(Guid tenantId, Guid userId, CancellationToken cancellationToken)
    {
        CallCount++;
        LastTenantId = tenantId;
        LastUserId = userId;
        return Task.FromResult(_anotherActiveAdministratorRemains);
    }
}
