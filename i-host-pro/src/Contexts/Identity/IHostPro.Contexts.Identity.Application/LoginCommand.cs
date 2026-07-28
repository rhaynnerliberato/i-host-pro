using IHostPro.BuildingBlocks.Application;

namespace IHostPro.Contexts.Identity.Application;

/// <summary>
/// Authenticates a user by e-mail/password within an explicit tenant
/// (Incremento 2 plan, Etapa 8). Implements <see cref="IBootstrapRequest"/>:
/// the tenant is not yet resolved when this command is dispatched — it
/// carries only the client-supplied slug, resolved by a future
/// <c>ITenantBootstrapResolver&lt;LoginCommand&gt;</c> (not implemented in
/// this step).
///
/// Both <see cref="Email"/> and <see cref="Password"/> are excluded from
/// <see cref="ToString"/> (overridden below, replacing the record's
/// compiler-generated one, which would otherwise print both in full) — this
/// also covers accidental exposure via string interpolation,
/// structured-logging templates that stringify the whole message, and the
/// debugger's default display (Incremento 2 plan, Etapa 9 -&gt; 10 pendência
/// 1: the e-mail is PII and must never appear in logs/telemetry in plain
/// text). No identifier derived from the e-mail is included either — even a
/// truncated SHA-256 digest of a low-entropy value like an e-mail address is
/// feasibly reversible by dictionary/enumeration attack, so it is not a safe
/// substitute for the plain value. If a real operational need for
/// correlating repeated attempts against the same account arises, the
/// correct mechanism is an HMAC-SHA-256 keyed with a dedicated telemetry
/// secret (never plain/truncated SHA-256) — not implemented here because no
/// such need exists yet (não invente requisitos ausentes).
///
/// <see cref="RequestContext"/> is internal, server-captured data (device/
/// browser/IP) — never bound from the public request body itself (see
/// <see cref="AuthenticationRequestContext"/>).
/// </summary>
public sealed record LoginCommand(
    string TenantSlug,
    string Email,
    string Password,
    AuthenticationRequestContext RequestContext) : ICommand<AuthTokensResult>, IBootstrapRequest
{
    public override string ToString() =>
        $"{nameof(LoginCommand)} {{ {nameof(TenantSlug)} = {TenantSlug}, {nameof(Email)} = [REDACTED], " +
        $"{nameof(Password)} = [REDACTED], {nameof(RequestContext)} = {RequestContext} }}";
}
