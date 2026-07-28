using IHostPro.BuildingBlocks.Messaging.Abstractions;

namespace IHostPro.Contexts.Identity.Contracts;

/// <summary>
/// Published when a login attempt succeeds (Incremento 2 plan, Etapa 15;
/// Documento 07 §13.1). <see cref="IntegrationEvent.AggregateId"/> is the
/// authenticated user's id, <see cref="IntegrationEvent.AggregateType"/> is
/// <c>"User"</c>. Never carries an e-mail, IP address, User-Agent, password,
/// access token, refresh token, or token hash — only identifiers.
/// </summary>
public sealed record UserLoggedIn : IntegrationEvent
{
    public required Guid SessionId { get; init; }
}
