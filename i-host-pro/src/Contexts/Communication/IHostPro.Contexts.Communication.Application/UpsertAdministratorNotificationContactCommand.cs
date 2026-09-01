using IHostPro.BuildingBlocks.Application;

namespace IHostPro.Contexts.Communication.Application;

/// <summary>
/// Creates or replaces the Tenant's single ACTIVE administrator notification
/// contact (Fase 11, Checkpoint 6, mandate item 20/22) — WhatsApp-only this
/// checkpoint. Dispatched exclusively through
/// <see cref="ICommunicationRequestDispatcher"/> from an administrative
/// endpoint in <c>IHostPro.Api</c> (guarded by <c>AI_AGENT:MANAGE</c>).
/// </summary>
public sealed record UpsertAdministratorNotificationContactCommand : ICommand<AdministratorNotificationContactResult>
{
    public required Guid TenantId { get; init; }

    public required string DestinationPhone { get; init; }
}
