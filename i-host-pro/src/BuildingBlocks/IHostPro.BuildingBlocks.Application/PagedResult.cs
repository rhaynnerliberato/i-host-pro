namespace IHostPro.BuildingBlocks.Application;

/// <summary>
/// A page of results from any bounded context's paginated administrative
/// listing (Incremento 3, Checkpoint 4/5 planning) — framework-neutral,
/// carries no EF Core/ASP.NET Core dependency, so Application-layer query
/// handlers across every context can return it directly.
/// </summary>
public sealed record PagedResult<T>(int Page, int PageSize, int TotalCount, IReadOnlyCollection<T> Items);
