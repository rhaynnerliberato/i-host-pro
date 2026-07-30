using IHostPro.BuildingBlocks.Application;
using IHostPro.Contexts.Identity.Domain.Enums;

namespace IHostPro.Contexts.Identity.Application.Users;

/// <summary>
/// The two read-only administrative user queries (Incremento 3, Checkpoint
/// 5). One interface for both — not split into a separate lister/getter —
/// since both are "read users with their current roles attached, avoiding
/// N+1" concerns that would otherwise duplicate the exact same join logic.
/// </summary>
public interface IUserAdministrationReader
{
    /// <summary>
    /// <paramref name="pageSize"/> is defensively clamped to the configured
    /// maximum by the implementation itself — never trusts the caller alone
    /// to have already enforced the bound (Incremento 3, Checkpoint 5,
    /// Section 7: "nunca executar consulta sem limite").
    /// </summary>
    Task<PagedResult<UserResult>> ListAsync(
        int page, int pageSize, string? search, UserStatus? status, CancellationToken cancellationToken);

    Task<UserResult?> GetByIdAsync(Guid userId, CancellationToken cancellationToken);
}
