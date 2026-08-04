namespace IHostPro.Contexts.PropertyManagement.Api.Contracts;

public sealed record PropertyOwnerResponse(Guid PropertyId, Guid OwnerUserId, DateTimeOffset CreatedAt);
