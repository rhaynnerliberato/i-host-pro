namespace IHostPro.Contexts.PropertyManagement.Api.Contracts;

public sealed record CreateCondominiumRequest(string? Name, AddressRequest? Address);
