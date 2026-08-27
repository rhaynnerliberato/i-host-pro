namespace IHostPro.Contexts.PropertyManagement.Api.Contracts;

public sealed record SetFrontDeskContactRequest(string? DisplayName, string? PhoneNumber, bool IsActive);
