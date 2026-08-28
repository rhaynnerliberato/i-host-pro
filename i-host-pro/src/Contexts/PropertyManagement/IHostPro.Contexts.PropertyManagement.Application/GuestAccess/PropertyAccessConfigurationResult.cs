namespace IHostPro.Contexts.PropertyManagement.Application.GuestAccess;

/// <summary>
/// Never carries the resolved credential VALUE — only the reference the
/// administrator configured, mirrors <c>WhatsAppIntegrationResult</c>'s own
/// secret-reference-only shape.
/// </summary>
public sealed record PropertyAccessConfigurationResult(
    Guid Id,
    Guid PropertyId,
    string? AccessCredentialSecretReference,
    string? AccessInstructions,
    bool IsActive,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);
