namespace IHostPro.Contexts.PropertyManagement.Api.Contracts;

public sealed record CondominiumDetailResponse(
    Guid Id, string Name, AddressResponse Address, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
