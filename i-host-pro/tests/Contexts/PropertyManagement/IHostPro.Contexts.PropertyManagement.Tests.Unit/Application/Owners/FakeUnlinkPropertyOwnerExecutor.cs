using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.PropertyManagement.Application.Owners;

namespace IHostPro.Contexts.PropertyManagement.Tests.Unit.Application.Owners;

/// <summary>Pass-through test double — see <c>FakeLinkPropertyOwnerExecutor</c>'s own doc comment.</summary>
internal sealed class FakeUnlinkPropertyOwnerExecutor : IUnlinkPropertyOwnerExecutor
{
    public Task<Result> ExecuteAsync(Func<Task<Result>> operation, CancellationToken cancellationToken) => operation();
}
