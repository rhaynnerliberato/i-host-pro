using IHostPro.BuildingBlocks.Application;

namespace IHostPro.Contexts.Identity.Application;

/// <summary>
/// Rotates a refresh token and issues a new access token (Incremento 2 plan,
/// Etapa 8). Implements <see cref="IBootstrapRequest"/>: the caller is not
/// authenticated yet — the tenant is resolved from the non-sensitive
/// tenant-id segment embedded in <see cref="RefreshToken"/> itself, by a
/// future <c>ITenantBootstrapResolver&lt;RefreshTokenCommand&gt;</c> (not
/// implemented in this step; see <c>IRefreshTokenParser</c>, Etapa 7).
///
/// <see cref="RefreshToken"/> is deliberately excluded from
/// <see cref="ToString"/> (overridden below, replacing the record's
/// compiler-generated one, which would otherwise print it in full).
/// </summary>
public sealed record RefreshTokenCommand(
    string RefreshToken,
    AuthenticationRequestContext RequestContext) : ICommand<AuthTokensResult>, IBootstrapRequest
{
    public override string ToString() =>
        $"{nameof(RefreshTokenCommand)} {{ {nameof(RefreshToken)} = [REDACTED], {nameof(RequestContext)} = {RequestContext} }}";
}
