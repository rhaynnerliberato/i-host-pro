using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.PropertyManagement.Application.Owners;

namespace IHostPro.Contexts.PropertyManagement.Tests.Unit.Application.Owners;

/// <summary>
/// A pass-through test double — this project uses no mocking library. Unlike
/// production, where <see cref="ILinkPropertyOwnerExecutor"/> opens a real
/// transaction, this simply invokes the operation directly: the handler unit
/// tests exercise business logic, not transactional mechanics (already
/// covered at the integration level).
/// </summary>
internal sealed class FakeLinkPropertyOwnerExecutor : ILinkPropertyOwnerExecutor
{
    public int CallCount { get; private set; }

    public Task<Result<PropertyOwnerResult>> ExecuteAsync(
        Func<Task<Result<PropertyOwnerResult>>> operation, CancellationToken cancellationToken)
    {
        CallCount++;
        return operation();
    }
}
