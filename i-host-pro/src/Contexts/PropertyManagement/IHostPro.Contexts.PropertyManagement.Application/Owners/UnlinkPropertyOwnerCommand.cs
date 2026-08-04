using IHostPro.BuildingBlocks.Application;

namespace IHostPro.Contexts.PropertyManagement.Application.Owners;

/// <summary>
/// Removes an owner's link to a property (Checkpoint 5 plan, item 8). No
/// re-validation of eligibility against Identity — removing a link never
/// depends on the owner's current role/status. No validator: both ids are
/// route-bound, already well-formed Guids by ASP.NET Core's routing
/// constraint, no further structural validation exists.
/// </summary>
public sealed record UnlinkPropertyOwnerCommand(Guid TenantId, Guid ActorId, Guid PropertyId, Guid OwnerUserId) : ICommand;
