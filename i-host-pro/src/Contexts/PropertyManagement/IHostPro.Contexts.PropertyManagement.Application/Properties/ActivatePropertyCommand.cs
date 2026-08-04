using IHostPro.BuildingBlocks.Application;

namespace IHostPro.Contexts.PropertyManagement.Application.Properties;

/// <summary>
/// Activates a property (<c>Draft</c>/<c>Inactive</c> → <c>Active</c> —
/// Checkpoint 4 plan, item 3). <see cref="TenantId"/>/<see cref="ActorId"/>
/// come exclusively from the authenticated Administrator's access token
/// claims. No body — <see cref="PropertyId"/> is the route-bound id, already
/// validated as a well-formed Guid by ASP.NET Core's routing constraint; no
/// further structural validation exists, so no validator is registered for
/// this command (Checkpoint 4 plan, item 8).
/// </summary>
public sealed record ActivatePropertyCommand(Guid TenantId, Guid ActorId, Guid PropertyId) : ICommand<PropertyResult>;
