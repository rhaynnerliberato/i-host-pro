namespace IHostPro.Contexts.PropertyManagement.Api.Contracts;

/// <summary>Never carries the resolved credential VALUE — only the reference the administrator configured (Fase 10, Checkpoint 6.2).</summary>
public sealed record PropertyAccessConfigurationResponse(
    Guid Id,
    Guid PropertyId,
    string? AccessCredentialSecretReference,
    string? AccessInstructions,
    bool IsActive,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);
