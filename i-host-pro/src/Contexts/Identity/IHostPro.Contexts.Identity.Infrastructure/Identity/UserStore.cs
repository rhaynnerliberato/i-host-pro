using IHostPro.Contexts.Identity.Domain;
using IHostPro.Contexts.Identity.Domain.ValueObjects;
using IHostPro.Contexts.Identity.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace IHostPro.Contexts.Identity.Infrastructure.Identity;

/// <summary>
/// Custom ASP.NET Core Identity store backed directly by the Domain's
/// <see cref="User"/> aggregate and `identity.users` — there is no separate
/// `ApplicationUser`/`AspNetUsers` table (Incremento 1 plan, Section 2).
///
/// <para>
/// <see cref="CreateAsync"/>/<see cref="UpdateAsync"/>/<see cref="DeleteAsync"/>
/// never call <c>SaveChangesAsync</c>: they only stage changes on the
/// IdentityDbContext's ChangeTracker. The single <c>SaveChangesAsync</c> call for the
/// whole use case happens exclusively inside
/// <c>ITenantAwareUnitOfWork.ExecuteAsync</c>, at the end of the tenant-aware
/// transaction — this store, and therefore any <c>UserManager&lt;User&gt;</c>
/// built on top of it, can never persist a change outside that boundary
/// (Incremento 1 plan, adendo final, Section 3).
/// </para>
///
/// <para>
/// Email doubles as the ASP.NET Core Identity "username" — <see cref="SetUserNameAsync"/>
/// funnels into the exact same domain mutator as <see cref="SetEmailAsync"/>;
/// there is no independent username concept, and no `user_name`/
/// `normalized_user_name` column exists (Incremento 1 plan, adendo final,
/// Section 2).
/// </para>
/// </summary>
public sealed class UserStore :
    IUserStore<User>,
    IUserPasswordStore<User>,
    IUserEmailStore<User>,
    IUserLockoutStore<User>,
    IUserSecurityStampStore<User>
{
    private readonly IdentityDbContext _dbContext;
    private readonly TimeProvider _timeProvider;

    public UserStore(IdentityDbContext dbContext, TimeProvider timeProvider)
    {
        _dbContext = dbContext;
        _timeProvider = timeProvider;
    }

    // --- IUserStore<User> ---

    public Task<string> GetUserIdAsync(User user, CancellationToken cancellationToken) =>
        Task.FromResult(user.Id.ToString());

    public Task<string?> GetUserNameAsync(User user, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(user.Email.Value);

    public Task SetUserNameAsync(User user, string? userName, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(userName))
            user.ChangeEmail(Email.Create(userName), _timeProvider.GetUtcNow());

        return Task.CompletedTask;
    }

    public Task<string?> GetNormalizedUserNameAsync(User user, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(user.Email.NormalizedValue);

    public Task SetNormalizedUserNameAsync(User user, string? normalizedName, CancellationToken cancellationToken) =>
        // No-op by design: there is no separate normalized-username column.
        // EmailLookupNormalizer guarantees whatever UserManager computed here
        // is already identical to user.Email.NormalizedValue.
        Task.CompletedTask;

    public Task<IdentityResult> CreateAsync(User user, CancellationToken cancellationToken)
    {
        _dbContext.Users.Add(user);
        return Task.FromResult(IdentityResult.Success);
    }

    public Task<IdentityResult> UpdateAsync(User user, CancellationToken cancellationToken)
    {
        // Defensive: in every realistic path the entity is already tracked
        // (loaded via this same IdentityDbContext instance earlier in the same unit
        // of work), so this is a no-op for the change tracker.
        _dbContext.Users.Update(user);
        return Task.FromResult(IdentityResult.Success);
    }

    public Task<IdentityResult> DeleteAsync(User user, CancellationToken cancellationToken)
    {
        _dbContext.Users.Remove(user);
        return Task.FromResult(IdentityResult.Success);
    }

    public async Task<User?> FindByIdAsync(string userId, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(userId, out var id))
            return null;

        return await _dbContext.Users.FirstOrDefaultAsync(u => u.Id == id, cancellationToken);
    }

    public async Task<User?> FindByNameAsync(string normalizedUserName, CancellationToken cancellationToken) =>
        await _dbContext.Users.FirstOrDefaultAsync(u => u.NormalizedEmail == normalizedUserName, cancellationToken);

    // --- IUserEmailStore<User> ---

    public Task SetEmailAsync(User user, string? email, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(email))
            user.ChangeEmail(Email.Create(email), _timeProvider.GetUtcNow());

        return Task.CompletedTask;
    }

    public Task<string?> GetEmailAsync(User user, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(user.Email.Value);

    public Task<bool> GetEmailConfirmedAsync(User user, CancellationToken cancellationToken) =>
        // No email confirmation flow exists in this phase — a user created
        // administratively is considered confirmed by construction.
        Task.FromResult(true);

    public Task SetEmailConfirmedAsync(User user, bool confirmed, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public async Task<User?> FindByEmailAsync(string normalizedEmail, CancellationToken cancellationToken) =>
        await _dbContext.Users.FirstOrDefaultAsync(u => u.NormalizedEmail == normalizedEmail, cancellationToken);

    public Task<string?> GetNormalizedEmailAsync(User user, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(user.Email.NormalizedValue);

    public Task SetNormalizedEmailAsync(User user, string? normalizedEmail, CancellationToken cancellationToken) =>
        // No-op — see SetNormalizedUserNameAsync.
        Task.CompletedTask;

    // --- IUserPasswordStore<User> ---

    public Task SetPasswordHashAsync(User user, string? passwordHash, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(passwordHash))
            user.SetPasswordHash(PasswordHash.FromEncoded(passwordHash), _timeProvider.GetUtcNow());

        return Task.CompletedTask;
    }

    public Task<string?> GetPasswordHashAsync(User user, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(user.PasswordHash.Value);

    public Task<bool> HasPasswordAsync(User user, CancellationToken cancellationToken) =>
        Task.FromResult(true);

    // --- IUserLockoutStore<User> ---

    public Task<DateTimeOffset?> GetLockoutEndDateAsync(User user, CancellationToken cancellationToken) =>
        Task.FromResult(user.LockoutEnd);

    public Task SetLockoutEndDateAsync(User user, DateTimeOffset? lockoutEnd, CancellationToken cancellationToken)
    {
        if (lockoutEnd.HasValue)
            user.LockUntil(lockoutEnd.Value, _timeProvider.GetUtcNow());
        else
            user.ClearLockout(_timeProvider.GetUtcNow());

        return Task.CompletedTask;
    }

    public Task<int> IncrementAccessFailedCountAsync(User user, CancellationToken cancellationToken)
    {
        user.IncrementFailedAccessCount(_timeProvider.GetUtcNow());
        return Task.FromResult(user.FailedAccessCount);
    }

    public Task ResetAccessFailedCountAsync(User user, CancellationToken cancellationToken)
    {
        user.ResetFailedAccessCount(_timeProvider.GetUtcNow());
        return Task.CompletedTask;
    }

    public Task<int> GetAccessFailedCountAsync(User user, CancellationToken cancellationToken) =>
        Task.FromResult(user.FailedAccessCount);

    public Task<bool> GetLockoutEnabledAsync(User user, CancellationToken cancellationToken) =>
        Task.FromResult(true);

    public Task SetLockoutEnabledAsync(User user, bool enabled, CancellationToken cancellationToken) =>
        // Lockout is always enabled for every user in this phase — no
        // documented requirement for a per-user opt-out exists.
        Task.CompletedTask;

    // --- IUserSecurityStampStore<User> ---

    public Task SetSecurityStampAsync(User user, string stamp, CancellationToken cancellationToken)
    {
        user.SetSecurityStamp(stamp, _timeProvider.GetUtcNow());
        return Task.CompletedTask;
    }

    public Task<string?> GetSecurityStampAsync(User user, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(user.SecurityStamp);

    // --- IDisposable ---

    public void Dispose()
    {
        // Deliberate no-op: the IdentityDbContext is a dependency injected into this
        // store, never created by it — disposing it here would break the
        // shared unit of work (Incremento 1 plan, adendo final, Section 3).
    }
}
