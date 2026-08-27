namespace IHostPro.Contexts.PropertyManagement.Api.Contracts;

public sealed record FrontDeskContactResponse(
    Guid Id,
    Guid CondominiumId,
    string DisplayName,
    string PhoneNumber,
    bool IsActive,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);
