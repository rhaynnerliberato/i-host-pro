using IHostPro.BuildingBlocks.Application;

namespace IHostPro.Contexts.ExternalIntegrations.Application.AirbnbEmailBridge;

/// <summary>
/// Starts the Web OAuth Authorization Code + PKCE flow (Web OAuth
/// architecture gate) — a NORMAL authenticated command, registered with the
/// standard Audit+TenantTransactionBehavior pair (unlike Connect/Disconnect):
/// nothing here calls an authenticator that manages its own nested
/// transaction, so no NestedUnitOfWorkException risk exists.
/// <see cref="TenantId"/>/<see cref="ActorUserId"/> come exclusively from the
/// caller's own already-authenticated JWT — never accepted as request body
/// fields (item 10 of the gate).
/// </summary>
public sealed record StartAirbnbEmailWebOAuthCommand(Guid TenantId, Guid ActorUserId) : ICommand<AirbnbEmailWebOAuthStartResult>;

public sealed record AirbnbEmailWebOAuthStartResult(string AuthorizationUrl);
