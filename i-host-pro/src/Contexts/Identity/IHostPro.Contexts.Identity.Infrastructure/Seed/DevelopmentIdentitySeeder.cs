using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.Contexts.Identity.Domain;
using IHostPro.Contexts.Identity.Domain.ValueObjects;
using IHostPro.Contexts.Identity.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IHostPro.Contexts.Identity.Infrastructure.Seed;

/// <summary>
/// Ensures the Development-only tenant/user described by
/// <see cref="DevelopmentSeedOptions"/> exists, so a developer can log in
/// locally without a manual SQL step (Incremento 2 plan, ajuste 3-4).
/// Registered only when the host environment is Development (see
/// <c>IdentityModuleExtensions.AddIdentityModule</c>) — never resolved,
/// never runs, outside it.
///
/// Runs once during host startup (<see cref="IHostedService.StartAsync"/>),
/// before the host starts accepting traffic. A misconfiguration (e.g. an
/// admin password that fails <see cref="IPasswordValidator{TUser}"/>) fails
/// the host startup fast rather than allowing a broken seed to sit silently.
///
/// Does not assign any role to the seeded user — role assignment stays out
/// of scope, exactly as it does everywhere else in this increment.
/// </summary>
public sealed class DevelopmentIdentitySeeder : IHostedService
{
    // Fixed, non-secret key — identifies this specific seeding operation
    // across every process that might run it concurrently (IHostPro.Api and
    // IHostPro.Worker both call AddIdentityModule, so both can host this
    // service in Development). pg_advisory_xact_lock is released
    // automatically at COMMIT/ROLLBACK, so a crashed process can never leave
    // the lock held.
    private const string AdvisoryLockKey = "ihostpro:identity:development-seed";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly DevelopmentSeedOptions _options;
    private readonly ILogger<DevelopmentIdentitySeeder> _logger;

    public DevelopmentIdentitySeeder(
        IServiceScopeFactory scopeFactory,
        IOptions<DevelopmentSeedOptions> options,
        ILogger<DevelopmentIdentitySeeder> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
            return;

        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        var tenantContext = scope.ServiceProvider.GetRequiredService<ITenantContext>();
        var passwordHasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<User>>();
        var passwordValidator = scope.ServiceProvider.GetRequiredService<IPasswordValidator<User>>();
        var timeProvider = scope.ServiceProvider.GetRequiredService<TimeProvider>();

        var tenantSlug = TenantSlug.Create(_options.TenantSlug);
        var adminEmail = Email.Create(_options.AdminEmail);

        await using var transaction = await dbContext.Database
            .BeginTransactionAsync(cancellationToken);

        // Serializes concurrent seed attempts from every process that might
        // run this at the same time (multiple Api/Worker instances starting
        // together) purely at the database level — no in-process
        // coordination exists or is needed. Held for the lifetime of this
        // transaction only.
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtext({AdvisoryLockKey}))",
            cancellationToken);

        var tenant = await dbContext.Tenants
            .FirstOrDefaultAsync(t => t.Slug == tenantSlug, cancellationToken);

        if (tenant is null)
        {
            tenant = Tenant.Provision(Guid.NewGuid(), tenantSlug, _options.TenantName, timeProvider.GetUtcNow());
            dbContext.Tenants.Add(tenant);
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        // Two independent tenant-isolation mechanisms both need the resolved
        // tenant id from here on, and neither is optional:
        // (1) BaseDbContext's EF Core Global Query Filter reads
        //     ITenantContext.TenantId directly (fail-closed to "no rows" when
        //     unresolved, by design) — without this, Users.AnyAsync/Add below
        //     would silently see zero rows every time, defeating the
        //     idempotency check.
        // (2) `users` carries FORCE ROW LEVEL SECURITY at the PostgreSQL
        //     level, independent of (1) — app.tenant_id must be set on this
        //     transaction, exactly as TenantAwareTransactionScope does for the
        //     request pipeline. Embedded directly via ExecuteSqlRawAsync
        //     (never ExecuteSqlInterpolatedAsync — PostgreSQL rejects bind
        //     parameters inside SET) is safe here because a Guid's "D" format
        //     only ever produces hex digits and hyphens.
        tenantContext.SetTenant(tenant.Id);
#pragma warning disable EF1002
        await dbContext.Database.ExecuteSqlRawAsync(
            $"SET LOCAL app.tenant_id = '{tenant.Id:D}'",
            cancellationToken);
#pragma warning restore EF1002

        var userExists = await dbContext.Users.AnyAsync(
            u => u.TenantId == tenant.Id && u.NormalizedEmail == adminEmail.NormalizedValue,
            cancellationToken);

        if (!userExists)
        {
            // The same policy source LoginCommandHandler/UserStore rely on —
            // never a second, divergent check (DevelopmentSeedOptionsValidator
            // deliberately only checks presence, not complexity, for exactly
            // this reason). manager/user are unused by PasswordPolicyValidator,
            // matching the established null!-argument convention already used
            // for IPasswordHasher<User>.HashPassword when no User exists yet.
            var passwordCheck = await passwordValidator.ValidateAsync(manager: null!, user: null!, _options.AdminPassword);
            if (!passwordCheck.Succeeded)
            {
                throw new InvalidOperationException(
                    "Identity:DevelopmentSeed:AdminPassword does not satisfy the configured password policy: " +
                    string.Join("; ", passwordCheck.Errors.Select(e => e.Description)) +
                    ". The value itself is never included in this message.");
            }

            var passwordHash = PasswordHash.FromEncoded(passwordHasher.HashPassword(null!, _options.AdminPassword));
            var user = User.Register(
                Guid.NewGuid(), tenant.Id, adminEmail, _options.AdminFullName, passwordHash, timeProvider.GetUtcNow());

            dbContext.Users.Add(user);
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        _logger.LogInformation(
            "Development identity seed ensured (tenant slug {TenantSlug}). No role was assigned.", _options.TenantSlug);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
