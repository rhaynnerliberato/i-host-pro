namespace IHostPro.Contexts.Identity.Application;

/// <summary>
/// Internal, server-captured context for a login/refresh attempt — never
/// bound directly from an untrusted HTTP request body (Incremento 2 plan,
/// Etapa 8). The future Api-layer controller captures these values itself
/// from the connection/headers (never from client-supplied JSON fields) and
/// constructs this type explicitly; <see cref="LoginCommand"/>/
/// <see cref="RefreshTokenCommand"/> never depend on <c>HttpContext</c>
/// directly, only on this small, already-extracted value.
///
/// None of these three fields is a secret — they are the same fingerprint
/// data already stored in plain columns on <c>Session</c> since Incremento 1
/// (<c>Device</c>, <c>Browser</c>, <c>IpAddress</c>), used here purely for
/// audit/future session-management purposes, never as a gate or challenge
/// mechanism (Incremento 2 plan, "estratégia de fingerprint de sessão").
/// </summary>
public sealed record AuthenticationRequestContext(string? IpAddress, string? Device, string? Browser);
