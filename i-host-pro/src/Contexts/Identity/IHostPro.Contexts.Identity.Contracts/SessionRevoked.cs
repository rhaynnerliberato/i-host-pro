using IHostPro.BuildingBlocks.Messaging.Abstractions;

namespace IHostPro.Contexts.Identity.Contracts;

/// <summary>
/// Published whenever a session is revoked. Only two triggers exist in this
/// increment (Incremento 2 plan, Etapa 15; Documento 07 §13.1) —
/// <see cref="ReasonCode"/> is one of the stable ASCII codes
/// <c>logout_requested</c> (alongside <see cref="UserLoggedOut"/>) or
/// <c>refresh_token_reuse_detected</c> (alongside
/// <see cref="RefreshTokenReuseDetected"/>). An administrative-revocation
/// trigger is not implemented yet — see <c>SecurityAuditReasonCode.AdminRevoked</c>'s
/// own doc comment.
///
/// <see cref="IntegrationEvent.AggregateId"/>/<see cref="IntegrationEvent.AggregateType"/>
/// are the user's id/<c>"User"</c>.
/// </summary>
public sealed record SessionRevoked : IntegrationEvent
{
    public required Guid SessionId { get; init; }

    public required string ReasonCode { get; init; }
}
