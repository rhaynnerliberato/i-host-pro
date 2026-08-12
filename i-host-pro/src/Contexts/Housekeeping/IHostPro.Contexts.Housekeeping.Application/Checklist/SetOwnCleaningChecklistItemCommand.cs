using IHostPro.BuildingBlocks.Application;

namespace IHostPro.Contexts.Housekeeping.Application.Checklist;

/// <summary>
/// Self-service checklist item toggle (Fase 6, Incremento 2A) —
/// <see cref="ActorId"/> is always the caller's own authenticated user id,
/// also the housekeeper identity the ABAC check compares against
/// <c>Cleaning.AssignedHousekeeperUserId</c>. <see cref="ItemType"/> is one
/// of <c>ChecklistItemTypeCodeMapper</c>'s stable codes. Never gates
/// <c>Cleaning.Complete</c> (approval §17).
/// </summary>
public sealed record SetOwnCleaningChecklistItemCommand(
    Guid TenantId, Guid ActorId, Guid CleaningId, string ItemType, bool IsChecked) : ICommand<CleaningChecklistItemResult>;
