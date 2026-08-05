using IHostPro.BuildingBlocks.Application;
using IHostPro.Contexts.Identity.Application.Users;
using IHostPro.Contexts.Identity.Domain.Enums;
using IHostPro.Contexts.Identity.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IHostPro.Contexts.Identity.Infrastructure.Users;

/// <inheritdoc cref="IUserAdministrationReader"/>
/// <remarks>
/// Reads exclusively from <see cref="IdentityDbContext"/> — <c>Users</c>/
/// <c>UserRoles</c> are both <c>ITenantOwned</c>, so the IdentityDbContext's Global
/// Query Filter already scopes every query here to the current tenant (same
/// reasoning as <c>UserRoleReader</c>/<c>SessionReader</c>). Role codes for a
/// page of users are fetched in exactly one extra query, grouped in memory —
/// never one query per user (Incremento 3, Checkpoint 5, Section 7: "evitar
/// N+1"). Ordering of role codes is computed in memory with
/// <see cref="StringComparer.Ordinal"/> after materializing, never trusting
/// PostgreSQL's default column collation — same reasoning as
/// <c>IdentityCatalogReader</c>.
/// </remarks>
public sealed class UserAdministrationReader : IUserAdministrationReader
{
    private readonly IdentityDbContext _dbContext;
    private readonly IUserListingSettingsProvider _settings;

    public UserAdministrationReader(IdentityDbContext dbContext, IUserListingSettingsProvider settings)
    {
        _dbContext = dbContext;
        _settings = settings;
    }

    public async Task<PagedResult<UserResult>> ListAsync(
        int page, int pageSize, string? search, UserStatus? status, CancellationToken cancellationToken)
    {
        var effectivePage = Math.Max(page, 1);
        var effectivePageSize = Math.Clamp(pageSize, 1, _settings.MaxPageSize);

        var query = _dbContext.Users.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(search))
        {
            var trimmedSearch = search.Trim();
            var normalizedSearch = trimmedSearch.ToLowerInvariant();
            query = query.Where(u =>
                EF.Functions.ILike(u.FullName, $"%{trimmedSearch}%") ||
                u.NormalizedEmail.Contains(normalizedSearch));
        }

        if (status.HasValue)
            query = query.Where(u => u.Status == status.Value);

        var totalCount = await query.CountAsync(cancellationToken);

        var users = await query
            .OrderBy(u => u.NormalizedEmail)
            .ThenBy(u => u.Id)
            .Skip((effectivePage - 1) * effectivePageSize)
            .Take(effectivePageSize)
            .ToListAsync(cancellationToken);

        var userIds = users.Select(u => u.Id).ToArray();
        var userRoles = await _dbContext.UserRoles
            .AsNoTracking()
            .Where(ur => userIds.Contains(ur.UserId))
            .ToListAsync(cancellationToken);

        var roleCodesByUserId = userRoles
            .GroupBy(ur => ur.UserId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyCollection<string>)group
                    .Select(ur => ur.RoleCode)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(code => code, StringComparer.Ordinal)
                    .ToArray());

        var items = users
            .Select(u => new UserResult(
                u.Id,
                u.FullName,
                u.Email.Value,
                u.Status.ToString(),
                roleCodesByUserId.TryGetValue(u.Id, out var codes) ? codes : Array.Empty<string>(),
                u.CreatedAt,
                u.LastLoginAt))
            .ToArray();

        return new PagedResult<UserResult>(effectivePage, effectivePageSize, totalCount, items);
    }

    public async Task<UserResult?> GetByIdAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = await _dbContext.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null)
            return null;

        var roleCodes = await _dbContext.UserRoles
            .AsNoTracking()
            .Where(ur => ur.UserId == userId)
            .Select(ur => ur.RoleCode)
            .ToListAsync(cancellationToken);

        var sortedRoleCodes = roleCodes.Distinct(StringComparer.Ordinal).OrderBy(code => code, StringComparer.Ordinal).ToArray();

        return new UserResult(user.Id, user.FullName, user.Email.Value, user.Status.ToString(), sortedRoleCodes, user.CreatedAt, user.LastLoginAt);
    }
}
