using IHostPro.BuildingBlocks.Messaging.Abstractions;

namespace IHostPro.Contexts.Identity.Contracts;

/// <summary>
/// Published when an Administrator creates a new user within the tenant
/// (Incremento 3 plan, Checkpoint 1; Documento 07 §13.3). Always published in
/// the same transaction as a <see cref="UserRoleAssigned"/> for the mandatory
/// initial role — a user cannot be created without a role in this phase
/// (Incremento 3 planning, approved decision) — whose
/// <see cref="IntegrationEvent.CausationId"/> carries this event's
/// <see cref="IntegrationEvent.EventId"/>.
///
/// <see cref="IntegrationEvent.AggregateId"/>/<see cref="IntegrationEvent.AggregateType"/>
/// are the new user's id/<c>"User"</c>. <see cref="IntegrationEvent.ActorId"/>
/// is the creating Administrator's user id. Never carries an e-mail,
/// password, or any credential — only identifiers.
/// </summary>
public sealed record UserCreated : IntegrationEvent;
