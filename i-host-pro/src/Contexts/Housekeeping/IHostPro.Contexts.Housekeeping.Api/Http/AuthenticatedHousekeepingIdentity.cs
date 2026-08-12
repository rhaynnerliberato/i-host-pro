namespace IHostPro.Contexts.Housekeeping.Api.Http;

/// <summary>
/// The two identifiers every Housekeeping action needs, read from the
/// caller's own validated access token claims — never from a request body,
/// route or query string. Mirrors
/// <c>Reservations.Api.Http.AuthenticatedReservationsIdentity</c> exactly.
/// See <see cref="HousekeepingIdentityReader"/>.
/// </summary>
public readonly record struct AuthenticatedHousekeepingIdentity(Guid UserId, Guid TenantId);
