namespace IHostPro.Contexts.Identity.Application;

/// <summary>
/// The two non-sensitive selectors extracted from a presented refresh token
/// string — never the secret segment (Incremento 2 plan, Etapa 7).
/// <see cref="TenantId"/> is untrusted at this point: it exists only to
/// bootstrap tenant resolution before <c>ITenantContext</c> is available
/// (Architecture Principles, "Bootstrap de autenticação"), never treated as
/// authenticated.
/// </summary>
public readonly record struct ParsedRefreshToken(Guid TenantId, Guid TokenId);

/// <summary>
/// Strictly parses the canonical refresh token format
/// (<c>{tenantId:N}.{tokenId:N}.{secret}</c>) coming from an untrusted
/// caller (Incremento 2 plan, Etapa 7). Never throws for malformed input —
/// every rejection reason (wrong segment count, non-canonical GUID, invalid
/// base64url, padding, excessive length, empty input) is reported uniformly
/// via a <see langword="false"/> return, never by inspecting *why* it failed,
/// and never by including the presented value in any diagnostic.
///
/// Does not decode or validate the secret segment's *length* against any
/// configured value, and never touches persistence or tenant resolution —
/// this step only extracts the two selectors.
/// </summary>
public interface IRefreshTokenParser
{
    bool TryParse(string? presentedToken, out ParsedRefreshToken parsed);
}
