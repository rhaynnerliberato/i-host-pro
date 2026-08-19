namespace IHostPro.Contexts.ExternalIntegrations.Api.Contracts;

/// <summary>
/// Never carries a secret value directly — the secret references here are
/// opaque names the caller assigns (e.g. matching a User Secrets/environment
/// variable key in Development), never the secret itself (CP2.1 mandate
/// §26: no endpoint receives a real secret and writes it to the database).
/// </summary>
public sealed record ConfigureWhatsAppIntegrationRequest(
    string? WabaId,
    string? PhoneNumberId,
    string? AccessTokenSecretReference,
    string? AppSecretSecretReference,
    string? VerifyTokenSecretReference);
