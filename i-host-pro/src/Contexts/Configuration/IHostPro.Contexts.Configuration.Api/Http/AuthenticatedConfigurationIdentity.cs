namespace IHostPro.Contexts.Configuration.Api.Http;

/// <summary>
/// The two identifiers every Configuration &amp; Policy action needs, read
/// from the caller's own validated access token claims — never from a
/// request body, route or query string. Mirrors
/// <c>Reservations.Api.Http.AuthenticatedReservationsIdentity</c> exactly.
/// See <see cref="ConfigurationIdentityReader"/>.
/// </summary>
public readonly record struct AuthenticatedConfigurationIdentity(Guid UserId, Guid TenantId);
