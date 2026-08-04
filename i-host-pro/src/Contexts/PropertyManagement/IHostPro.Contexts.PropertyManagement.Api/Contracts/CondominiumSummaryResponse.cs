namespace IHostPro.Contexts.PropertyManagement.Api.Contracts;

/// <summary>Deliberately excludes <c>Address</c> (Checkpoint 2 plan, item 4).</summary>
public sealed record CondominiumSummaryResponse(Guid Id, string Name, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
