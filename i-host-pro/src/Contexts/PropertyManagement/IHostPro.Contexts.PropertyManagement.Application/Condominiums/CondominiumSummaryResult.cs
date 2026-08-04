namespace IHostPro.Contexts.PropertyManagement.Application.Condominiums;

/// <summary>
/// A condominium as it appears in the paginated administrative listing
/// (Checkpoint 2 plan, item 4) — deliberately excludes <c>Address</c>: the
/// listing query itself never fetches it, so omitting it here is a real
/// guarantee, not just an HTTP-layer projection.
/// </summary>
public sealed record CondominiumSummaryResult(Guid Id, string Name, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
