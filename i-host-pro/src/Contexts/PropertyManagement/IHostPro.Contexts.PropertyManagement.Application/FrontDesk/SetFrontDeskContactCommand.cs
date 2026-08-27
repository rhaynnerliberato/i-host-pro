using IHostPro.BuildingBlocks.Application;

namespace IHostPro.Contexts.PropertyManagement.Application.FrontDesk;

/// <summary>
/// Creates or updates (upserts) the single active front desk contact for a
/// Condominium (Fase 10, Checkpoint 4 — "at most one active FrontDeskContact
/// per Condominium", user-decided cardinality). <see cref="TenantId"/>/
/// <see cref="ActorId"/> come exclusively from the authenticated
/// Administrator's access token claims. Idempotent: a request whose fields
/// all already match the current stored value mutates nothing, records no
/// audit entry — mirrors <c>UpdateCondominiumCommand</c>'s own idempotency
/// discipline.
/// </summary>
public sealed record SetFrontDeskContactCommand(
    Guid TenantId, Guid ActorId, Guid CondominiumId, string DisplayName, string PhoneNumber, bool IsActive)
    : ICommand<FrontDeskContactResult>;
