using IHostPro.BuildingBlocks.Application;

namespace IHostPro.Contexts.PropertyManagement.Application.Condominiums;

/// <summary>
/// Updates a condominium's name and/or address, idempotently (Checkpoint 2
/// plan, item 6/7). <see cref="TenantId"/>/<see cref="ActorId"/> come
/// exclusively from the authenticated Administrator's access token claims.
/// <see cref="Name"/>/<see cref="Address"/> are each independently optional —
/// <c>null</c> means "not supplied, leave unchanged". At least one of the two
/// must be non-null (Checkpoint 2 plan, item 7) — checked by the handler, not
/// the validator, mirroring <c>UpdateUserCommand</c>'s
/// <c>Identity.NoChangesProvided</c> precedent. When <see cref="Address"/> is
/// supplied, it always replaces the whole address — never a partial patch.
/// </summary>
public sealed record UpdateCondominiumCommand(
    Guid TenantId, Guid ActorId, Guid CondominiumId, string? Name, CondominiumAddressInput? Address)
    : ICommand<CondominiumResult>;
