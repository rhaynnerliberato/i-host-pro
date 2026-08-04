namespace IHostPro.Contexts.PropertyManagement.Api.Contracts;

public sealed record PropertySummaryResponse(
    Guid Id,
    string Code,
    string Name,
    int Capacity,
    Guid? CondominiumId,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
