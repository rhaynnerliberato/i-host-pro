using IHostPro.BuildingBlocks.Application;

namespace IHostPro.Contexts.Communication.Application;

/// <summary>Fase 11, Checkpoint 6 — reads the Tenant's ACTIVE administrator notification contact, if any.</summary>
public sealed record GetAdministratorNotificationContactQuery : IQuery<AdministratorNotificationContactResult?>
{
    public required Guid TenantId { get; init; }
}
