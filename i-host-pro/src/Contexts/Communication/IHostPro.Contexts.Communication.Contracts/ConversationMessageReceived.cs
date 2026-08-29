using IHostPro.BuildingBlocks.Messaging.Abstractions;

namespace IHostPro.Contexts.Communication.Contracts;

/// <summary>
/// Published when Communication persists an inbound guest <c>Message</c>
/// (Fase 11, Checkpoint 2 — AI Agent Foundation, mandate item 8). The first
/// Integration Event Communication ever publishes — deliberately deferred
/// since Fase 9, Checkpoint 1 (no real consumer existed until now, per
/// <c>CommunicationDbContext</c>/<c>ICommunicationTransactionExecutor</c>'s
/// own "absent a real consumer" doc comments) and Fase 11, Checkpoint 1
/// ("É aceitável CP1 terminar com inbound persisted e event contract pronto
/// para CP2 somente se source architecture exigir").
///
/// Deliberately minimal — never carries message content, credential, PIX QR,
/// guest phone, or any provider payload (mandate item 8). The AI Agent
/// Bounded Context resolves the actual (sanitized) content separately,
/// synchronously, through a purpose-limited reader — never through this
/// event's own payload.
///
/// <see cref="IntegrationEvent.AggregateId"/>/<see cref="IntegrationEvent.AggregateType"/>:
/// the persisted <c>Message</c>'s own id/"Message" — Communication's first
/// real aggregate identity exposed via an Integration Event.
/// <see cref="IntegrationEvent.CorrelationId"/>/<see cref="IntegrationEvent.CausationId"/>:
/// a fresh id per event (the inbound webhook that ultimately produced this
/// Message carries no correlation id of its own that crosses this
/// boundary). <see cref="IntegrationEvent.ActorType"/> is always
/// <c>"System"</c> (the guest's own message triggered this via choreography,
/// never a direct human actor inside the platform).
/// </summary>
public sealed record ConversationMessageReceived : IntegrationEvent
{
    public required Guid ConversationId { get; init; }

    public required Guid ReservationId { get; init; }

    public required Guid MessageId { get; init; }

    public required DateTimeOffset OccurredAtUtc { get; init; }
}
