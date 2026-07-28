using IHostPro.BuildingBlocks.Messaging.Abstractions;

namespace IHostPro.Contexts.Identity.Contracts;

/// <summary>
/// Published when a login attempt is rejected for any internal reason
/// (unknown e-mail, blocked user, already-locked-out account, wrong
/// password) — never for an unresolved/inactive tenant, which is recorded
/// only in structured telemetry, never as an Integration Event (Incremento 2
/// plan, Etapa 15; Documento 07 §13.1).
///
/// <see cref="IntegrationEvent.AggregateId"/>/<see cref="IntegrationEvent.AggregateType"/>
/// are the resolved tenant's id/<c>"Tenant"</c> here — not the user — because
/// <see cref="UserId"/> can be null (no user exists for the attempted
/// e-mail), while the tenant is always known by the time this event can be
/// published. Never carries an e-mail, IP address, User-Agent, password, or
/// any credential — only <see cref="ReasonCode"/>, one of the stable ASCII
/// codes catalogued in Documento 07 §13.1 (<c>user_not_found</c>,
/// <c>user_blocked</c>, <c>invalid_password</c>, <c>account_locked</c>).
/// </summary>
public sealed record LoginFailed : IntegrationEvent
{
    public Guid? UserId { get; init; }

    public required string ReasonCode { get; init; }
}
