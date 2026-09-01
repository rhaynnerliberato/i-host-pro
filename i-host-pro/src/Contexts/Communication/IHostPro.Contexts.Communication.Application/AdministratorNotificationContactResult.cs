namespace IHostPro.Contexts.Communication.Application;

/// <summary>
/// Shared by <see cref="UpsertAdministratorNotificationContactCommand"/> and
/// <see cref="GetAdministratorNotificationContactQuery"/> (Fase 11,
/// Checkpoint 6) — the administrative management surface for the contact
/// itself, deliberately distinct from <see cref="SendHumanHandoffNotificationCommand"/>'s
/// own contract, which never returns or accepts a phone number at all.
/// </summary>
public sealed record AdministratorNotificationContactResult(
    Guid Id, Guid TenantId, string DestinationPhone, bool IsActive, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);
