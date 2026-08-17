namespace IHostPro.Contexts.Dashboard.Api.Http;

/// <summary>
/// The two identifiers every Dashboard action needs, read from the caller's
/// own validated access token claims — never from a request body, route or
/// query string. Mirrors <c>Reservations.Api.Http.AuthenticatedReservationsIdentity</c>
/// exactly. See <see cref="DashboardIdentityReader"/>.
/// </summary>
public readonly record struct AuthenticatedDashboardIdentity(Guid UserId, Guid TenantId);
