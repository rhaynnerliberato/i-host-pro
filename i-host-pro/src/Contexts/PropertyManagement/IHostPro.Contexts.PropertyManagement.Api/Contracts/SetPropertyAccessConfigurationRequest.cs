namespace IHostPro.Contexts.PropertyManagement.Api.Contracts;

/// <summary>
/// Never accepts a raw credential value — <see cref="AccessCredentialSecretReference"/>
/// is a reference the administrator picks (e.g. a User Secrets/environment
/// variable key name); the actual secret value is configured out-of-band,
/// never through this HTTP surface (Fase 10, Checkpoint 6.2).
/// </summary>
public sealed record SetPropertyAccessConfigurationRequest(
    string? AccessCredentialSecretReference, string? AccessInstructions, bool IsActive);
