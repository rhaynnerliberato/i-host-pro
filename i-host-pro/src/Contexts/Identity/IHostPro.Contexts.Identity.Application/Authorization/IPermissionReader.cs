namespace IHostPro.Contexts.Identity.Application.Authorization;

/// <summary>
/// Resolves the distinct permission codes granted by a set of role codes, via
/// the platform's fixed Role → Permission catalog (Documento 09; RBAC decided
/// in ADR-005/ADR-012, implemented starting Incremento 3, Checkpoint 2).
/// Framework-neutral — no ASP.NET Core, EF Core or Infrastructure type
/// appears in this contract, so <c>PermissionAuthorizationHandler</c>
/// (Identity.Api) can depend on it without ever referencing
/// <c>Identity.Infrastructure</c> directly.
///
/// PostgreSQL (via the concrete implementation in Identity.Infrastructure) is
/// the only source of truth for the answer. An implementation may cache
/// results, but every value it ever returns must trace back to a real read
/// of the persisted catalog — never a parallel hardcoded list — and a
/// transient failure reading that source must propagate as an exception,
/// never be swallowed into an empty (and therefore falsely "no permission
/// granted") result.
/// </summary>
public interface IPermissionReader
{
    /// <summary>
    /// Role codes are compared using ordinal, case-sensitive matching against
    /// the canonical codes already established by <c>IdentityCatalogSeed</c>
    /// (e.g. <c>"ADMIN"</c>) — never normalized, trimmed beyond exact
    /// matching, or matched case-insensitively. An empty
    /// <paramref name="roleCodes"/>, an unknown role code, or a role code
    /// granting no permission yields no entries for that role — never an
    /// error.
    /// </summary>
    Task<IReadOnlyCollection<string>> GetPermissionCodesAsync(
        IReadOnlyCollection<string> roleCodes, CancellationToken cancellationToken);
}
