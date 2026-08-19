using IHostPro.BuildingBlocks.Application;

namespace IHostPro.Contexts.ExternalIntegrations.Application.WhatsAppIntegrations;

/// <summary>
/// Creates or updates the tenant's single WhatsApp integration (upsert —
/// exactly one row per tenant, never a separate create/update pair). Never
/// touches <c>IsEnabled</c> — no command in this checkpoint can enable a
/// real integration (CP2.1 mandate §18).
///
/// <see cref="ActorUserId"/> mirrors <c>CreatePolicyValueVersionCommand.ActorId</c>'s
/// own established precedent (Configuration.Application) — the caller's
/// already-authenticated user id, read from claims by the controller and
/// passed as a plain primitive, never <c>HttpContext</c>/<c>ClaimsPrincipal</c>.
/// Consumed exclusively by <see cref="AuditConfigureWhatsAppIntegrationBehavior"/>
/// (CP2.1.1 — Documento 17 §28-proportional structured audit).
/// </summary>
public sealed record ConfigureWhatsAppIntegrationCommand(
    Guid TenantId,
    Guid ActorUserId,
    string? WabaId,
    string? PhoneNumberId,
    string? AccessTokenSecretReference,
    string? AppSecretSecretReference,
    string? VerifyTokenSecretReference) : ICommand<WhatsAppIntegrationResult>;
