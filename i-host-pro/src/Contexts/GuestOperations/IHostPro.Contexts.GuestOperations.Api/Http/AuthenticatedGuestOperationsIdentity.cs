namespace IHostPro.Contexts.GuestOperations.Api.Http;

/// <summary>
/// The two identifiers every Guest Operations action needs, read from the
/// caller's own validated access token claims — never from a request body,
/// route or query string. Mirrors
/// <c>Reservations.Api.Http.AuthenticatedReservationsIdentity</c> exactly.
/// See <see cref="GuestOperationsIdentityReader"/>.
/// </summary>
public readonly record struct AuthenticatedGuestOperationsIdentity(Guid UserId, Guid TenantId);
