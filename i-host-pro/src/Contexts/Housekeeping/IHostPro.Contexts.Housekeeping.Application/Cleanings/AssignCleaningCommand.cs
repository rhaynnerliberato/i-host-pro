using IHostPro.BuildingBlocks.Application;

namespace IHostPro.Contexts.Housekeeping.Application.Cleanings;

/// <summary>
/// Assigns a cleaning to a Housekeeper — <c>Pending</c> → <c>Assigned</c>
/// (Fase 6, Incremento 1). <see cref="TenantId"/>/<see cref="ActorId"/> come
/// exclusively from the authenticated Administrator/Operator's access token
/// claims. <see cref="HousekeeperUserId"/> is validated via
/// <c>IIdentityUserEligibilityReader</c> — must exist, be active, belong to
/// the tenant, and hold the <c>HOUSEKEEPER</c> role.
/// </summary>
public sealed record AssignCleaningCommand(
    Guid TenantId,
    Guid ActorId,
    Guid CleaningId,
    Guid HousekeeperUserId) : ICommand<CleaningResult>;
