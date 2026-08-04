using IHostPro.BuildingBlocks.Application;

namespace IHostPro.Contexts.PropertyManagement.Application.Owners;

/// <summary>
/// Links a user as an owner of a property, after a synchronous eligibility
/// check against Identity's public contract (Checkpoint 5 plan, item 3/6/7).
/// <see cref="TenantId"/>/<see cref="ActorId"/> come exclusively from the
/// authenticated Administrator's access token claims; <see cref="PropertyId"/>
/// is the route-bound id; <see cref="OwnerUserId"/> is the only client-supplied
/// field, from the request body.
/// </summary>
public sealed record LinkPropertyOwnerCommand(
    Guid TenantId, Guid ActorId, Guid PropertyId, Guid OwnerUserId) : ICommand<PropertyOwnerResult>;
