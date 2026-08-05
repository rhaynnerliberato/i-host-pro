namespace IHostPro.Contexts.Reservations.Api.Http;

/// <summary>
/// The two identifiers every Reservations action needs, read from the
/// caller's own validated access token claims — never from a request body,
/// route or query string. Mirrors
/// <c>PropertyManagement.Api.Http.AuthenticatedPropertyManagementIdentity</c>
/// exactly. See <see cref="ReservationsIdentityReader"/>.
/// </summary>
public readonly record struct AuthenticatedReservationsIdentity(Guid UserId, Guid TenantId);
