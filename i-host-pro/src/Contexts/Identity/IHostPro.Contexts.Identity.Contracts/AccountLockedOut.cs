using IHostPro.BuildingBlocks.Messaging.Abstractions;

namespace IHostPro.Contexts.Identity.Contracts;

/// <summary>
/// Published for the specific login attempt that newly triggers an account
/// lockout — never for a subsequent attempt against an already-locked
/// account, which publishes <see cref="LoginFailed"/> with reason code
/// <c>account_locked</c> instead (Incremento 2 plan, Etapa 15; Documento 07
/// §13.1). Always published alongside a <see cref="LoginFailed"/> for the
/// same attempt.
///
/// <see cref="IntegrationEvent.AggregateId"/>/<see cref="IntegrationEvent.AggregateType"/>
/// are the user's id/<c>"User"</c> — the user always exists for this event
/// (an unknown e-mail can never trigger a lockout). Never carries an e-mail,
/// IP address, User-Agent, or password.
/// </summary>
public sealed record AccountLockedOut : IntegrationEvent
{
    public required DateTimeOffset LockoutEnd { get; init; }

    public required string ReasonCode { get; init; }
}
