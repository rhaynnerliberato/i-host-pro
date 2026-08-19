namespace IHostPro.Contexts.ExternalIntegrations.Api.Http;

/// <summary>
/// The two identifiers every WhatsApp integration action needs, read from
/// the caller's own validated access token claims — never from a request
/// body, route or query string. Mirrors
/// <c>Configuration.Api.Http.AuthenticatedConfigurationIdentity</c> exactly.
/// See <see cref="ExternalIntegrationsIdentityReader"/>.
/// </summary>
public readonly record struct AuthenticatedExternalIntegrationsIdentity(Guid UserId, Guid TenantId);
