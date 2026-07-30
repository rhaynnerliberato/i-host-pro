using IHostPro.BuildingBlocks.Messaging.Abstractions;

namespace IHostPro.Contexts.Identity.Contracts;

/// <summary>
/// Published when an Administrator unblocks a previously blocked user
/// (Incremento 3 plan, Checkpoint 1; Documento 07 §13.3). Deliberately never
/// folded into <see cref="UserUpdated"/> — unblocking is an
/// authorization-relevant fact, not a profile edit. Never restores or
/// recreates any session revoked by the prior <see cref="UserBlocked"/>; the
/// user must authenticate again to obtain a new one.
///
/// <see cref="IntegrationEvent.AggregateId"/>/<see cref="IntegrationEvent.AggregateType"/>
/// are the unblocked user's id/<c>"User"</c>. <see cref="IntegrationEvent.ActorId"/>
/// is the unblocking Administrator's user id.
/// </summary>
public sealed record UserUnblocked : IntegrationEvent;
