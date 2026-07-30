using IHostPro.Contexts.Identity.Application;
using IHostPro.Contexts.Identity.Domain;

namespace IHostPro.Contexts.Identity.Tests.Unit.Application.Users;

/// <summary>Hand-written test double — this project uses no mocking library, consistent with the rest of the solution.</summary>
internal sealed class FakeUserRoleWriter : IUserRoleWriter
{
    public List<UserRole> Assigned { get; } = [];
    public List<UserRole> Removed { get; } = [];

    public void Assign(UserRole userRole) => Assigned.Add(userRole);

    public void Remove(UserRole userRole) => Removed.Add(userRole);
}
