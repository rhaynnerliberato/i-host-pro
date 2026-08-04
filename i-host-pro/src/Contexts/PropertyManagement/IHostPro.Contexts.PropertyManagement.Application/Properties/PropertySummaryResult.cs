namespace IHostPro.Contexts.PropertyManagement.Application.Properties;

/// <summary>
/// A property as it appears in the paginated administrative listing
/// (Checkpoint 3 plan, item 8) — deliberately excludes own/effective
/// address: the listing query itself never fetches it (Checkpoint 3 plan,
/// item 9).
/// </summary>
public sealed record PropertySummaryResult(
    Guid Id,
    string Code,
    string Name,
    int Capacity,
    Guid? CondominiumId,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
