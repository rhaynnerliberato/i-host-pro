using IHostPro.Contexts.Communication.Domain;

namespace IHostPro.Contexts.Communication.Application;

/// <summary>Fase 11, Checkpoint 6.</summary>
public interface IAdministratorNotificationContactRepository
{
    void Add(AdministratorNotificationContact contact);

    void Update(AdministratorNotificationContact contact);

    /// <summary>The single lookup a human-handoff notification needs — the currently ACTIVE contact for a Tenant, if any (mandate item 20 — at most one exists, backstopped by a partial unique index).</summary>
    Task<AdministratorNotificationContact?> GetActiveByTenantIdAsync(Guid tenantId, CancellationToken cancellationToken);
}
