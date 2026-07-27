namespace IHostPro.BuildingBlocks.Application;

/// <summary>
/// Marks a request that never mutates state, so the tenant-aware unit of work can
/// open its transaction in read-only mode (`SET TRANSACTION READ ONLY`),
/// catching an accidental Command/Query separation violation at the database
/// level. Implemented by <see cref="IQuery{TResponse}"/>; commands never
/// implement it.
/// </summary>
public interface IReadOnlyRequest
{
}
