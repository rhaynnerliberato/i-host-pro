using IHostPro.BuildingBlocks.Application;

namespace IHostPro.Contexts.PropertyManagement.Application.GuestAccess;

/// <summary>
/// Creates or updates (upserts) the single guest-access configuration for a
/// Property (Fase 10, Checkpoint 6.2 — "at most one PropertyAccessConfiguration
/// per Property"). <see cref="TenantId"/>/<see cref="ActorId"/> come
/// exclusively from the authenticated Administrator's access token claims.
/// <see cref="AccessCredentialSecretReference"/> is a REFERENCE picked by the
/// administrator (e.g. a User Secrets/environment variable key name) — never
/// the raw credential value itself, which this command never carries and
/// this Bounded Context never persists. Idempotent: a request whose fields
/// all already match the current stored value mutates nothing, records no
/// audit entry — mirrors <c>SetFrontDeskContactCommand</c>'s own idempotency
/// discipline.
/// </summary>
public sealed record SetPropertyAccessConfigurationCommand(
    Guid TenantId, Guid ActorId, Guid PropertyId, string? AccessCredentialSecretReference,
    string? AccessInstructions, bool IsActive)
    : ICommand<PropertyAccessConfigurationResult>;
