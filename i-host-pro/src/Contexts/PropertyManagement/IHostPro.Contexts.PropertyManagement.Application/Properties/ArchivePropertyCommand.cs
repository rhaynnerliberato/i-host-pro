using IHostPro.BuildingBlocks.Application;

namespace IHostPro.Contexts.PropertyManagement.Application.Properties;

/// <summary>
/// Archives a property (<c>Draft</c>/<c>Inactive</c> → <c>Archived</c> —
/// terminal in this increment, Checkpoint 4 plan, item 3). Same claim-sourced
/// actor/tenant convention as <see cref="ActivatePropertyCommand"/>; no
/// validator (no body, no structural validation beyond the route-bound id).
/// </summary>
public sealed record ArchivePropertyCommand(Guid TenantId, Guid ActorId, Guid PropertyId) : ICommand<PropertyResult>;
