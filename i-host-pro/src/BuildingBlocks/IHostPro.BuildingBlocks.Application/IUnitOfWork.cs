namespace IHostPro.BuildingBlocks.Application;

/// <summary>
/// Persists the changes made during a use case as a single atomic operation.
/// Each Bounded Context's Infrastructure layer provides the concrete implementation
/// (its own EF Core DbContext) — this interface carries no persistence-technology
/// knowledge and contains no business vocabulary.
/// </summary>
public interface IUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
