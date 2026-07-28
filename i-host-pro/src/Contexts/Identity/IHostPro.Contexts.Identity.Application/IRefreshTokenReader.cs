using IHostPro.Contexts.Identity.Domain;

namespace IHostPro.Contexts.Identity.Application;

/// <summary>
/// Locates a <see cref="RefreshToken"/> by its public <c>TokenId</c> selector
/// (Incremento 2 plan, Etapa 10) — distinct from
/// <c>IRepository&lt;RefreshToken, Guid&gt;.GetByIdAsync</c>, which looks up
/// by the aggregate's internal primary key, never the value embedded in the
/// presented token string. The returned instance is tracked by the same
/// <c>DbContext</c> as the repository (both operate on the same underlying
/// `identity.refresh_tokens` table within the current tenant-scoped
/// transaction), so calling a domain mutator on it (e.g. <c>MarkRotated</c>)
/// is picked up by <c>SaveChangesAsync</c> without a separate <c>Update</c>
/// call.
/// </summary>
public interface IRefreshTokenReader
{
    Task<RefreshToken?> FindByTokenIdAsync(Guid tokenId, CancellationToken cancellationToken);

    /// <summary>
    /// Refresh tokens belonging to a session that are neither revoked nor
    /// expired as of <paramref name="now"/> (Incremento 2 plan, Etapa 11 —
    /// Logout revokes only these, leaving already-Rotated/Expired/
    /// previously-revoked tokens' own reason untouched by never even loading
    /// them here).
    /// </summary>
    Task<IReadOnlyCollection<RefreshToken>> FindActiveBySessionIdAsync(
        Guid sessionId, DateTimeOffset now, CancellationToken cancellationToken);
}
