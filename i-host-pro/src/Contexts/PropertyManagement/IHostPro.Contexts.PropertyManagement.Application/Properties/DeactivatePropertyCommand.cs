using IHostPro.BuildingBlocks.Application;

namespace IHostPro.Contexts.PropertyManagement.Application.Properties;

/// <summary>
/// Deactivates a property (<c>Active</c> → <c>Inactive</c> — Checkpoint 4
/// plan, item 3). Same claim-sourced actor/tenant convention as
/// <see cref="ActivatePropertyCommand"/>; no validator (no body, no
/// structural validation beyond the route-bound id).
/// </summary>
public sealed record DeactivatePropertyCommand(Guid TenantId, Guid ActorId, Guid PropertyId) : ICommand<PropertyResult>;
