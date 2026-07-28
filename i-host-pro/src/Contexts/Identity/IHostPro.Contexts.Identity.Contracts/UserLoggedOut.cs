using IHostPro.BuildingBlocks.Messaging.Abstractions;

namespace IHostPro.Contexts.Identity.Contracts;

/// <summary>
/// Published only for an effective logout — a session that already does not
/// exist, belongs to a different user, or is already inactive produces no
/// event at all, matching <c>LogoutCommandHandler</c>'s idempotent no-op
/// (Incremento 2 plan, Etapa 15; Documento 07 §13.1). Always published
/// alongside a <see cref="SessionRevoked"/> for the same session.
///
/// <see cref="IntegrationEvent.AggregateId"/>/<see cref="IntegrationEvent.AggregateType"/>
/// are the user's id/<c>"User"</c>.
/// </summary>
public sealed record UserLoggedOut : IntegrationEvent
{
    public required Guid SessionId { get; init; }
}
