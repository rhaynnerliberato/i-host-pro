using IHostPro.BuildingBlocks.Messaging.Abstractions;

namespace IHostPro.Contexts.Identity.Contracts;

/// <summary>
/// Published when an Administrator blocks a user (Incremento 3 plan,
/// Checkpoint 1; Documento 07 §13.3). Always published alongside one
/// <see cref="SessionRevoked"/> (reason code <c>user_blocked</c>) per session
/// that was active at the time of the block. Unblocking never restores those
/// sessions — see <see cref="UserUnblocked"/>.
///
/// <see cref="IntegrationEvent.AggregateId"/>/<see cref="IntegrationEvent.AggregateType"/>
/// are the blocked user's id/<c>"User"</c>. <see cref="IntegrationEvent.ActorId"/>
/// is the blocking Administrator's user id.
/// </summary>
public sealed record UserBlocked : IntegrationEvent;
