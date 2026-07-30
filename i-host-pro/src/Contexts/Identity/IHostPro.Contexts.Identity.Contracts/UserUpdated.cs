using IHostPro.BuildingBlocks.Messaging.Abstractions;

namespace IHostPro.Contexts.Identity.Contracts;

/// <summary>
/// Published when an Administrator updates a user's profile fields (name
/// and/or e-mail) — never for a role, block/unblock, or password change, each
/// of which has its own dedicated event (Incremento 3 plan, Checkpoint 1;
/// Documento 07 §13.3).
///
/// <see cref="ChangedFields"/> names which fields changed — the stable codes
/// <c>"full_name"</c>/<c>"email"</c> (Incremento 3, Checkpoint 8) — without
/// ever carrying their new value; an e-mail is forbidden content for any
/// Identity Integration Event (Documento 07 §13.1).
///
/// <see cref="IntegrationEvent.AggregateId"/>/<see cref="IntegrationEvent.AggregateType"/>
/// are the updated user's id/<c>"User"</c>. <see cref="IntegrationEvent.ActorId"/>
/// is the Administrator who made the change.
/// </summary>
public sealed record UserUpdated : IntegrationEvent
{
    public required IReadOnlyCollection<string> ChangedFields { get; init; }
}
