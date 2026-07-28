using System.Security.Cryptography;
using FluentAssertions;
using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.BuildingBlocks.Infrastructure.Persistence;
using IHostPro.Contexts.Identity.Application;
using IHostPro.Contexts.Identity.Domain;
using IHostPro.Contexts.Identity.Domain.Enums;
using IHostPro.Contexts.Identity.Domain.ValueObjects;
using IHostPro.Contexts.Identity.Infrastructure;
using IHostPro.Contexts.Identity.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Npgsql;
using Testcontainers.PostgreSql;

namespace IHostPro.Contexts.Identity.Tests.Integration;

/// <summary>
/// End-to-end test of the Login use case against a real PostgreSQL instance
/// (Incremento 2 plan, Etapa 9): <see cref="LoginTenantBootstrapResolver"/> +
/// <see cref="LoginCommandHandler"/>, manually chained exactly the way
/// <c>TenantBootstrapBehavior</c> would (no <c>AddMediator()</c> wiring
/// exists in this solution yet — see <c>IdentityModuleExtensions</c>) so the
/// real registration graph (<c>AddIdentityModule</c> +
/// <c>AddIdentityJwtIssuance</c>) is exercised, not a hand-picked subset.
/// </summary>
public class LoginCommandHandlerTests : IClassFixture<LoginCommandHandlerTests.Fixture>
{
    private const string KnownPassword = "Correct-Horse-Battery-Staple-42!";

    private readonly string _migratorConnectionString;
    private readonly string _appConnectionString;

    public LoginCommandHandlerTests(Fixture fixture)
    {
        _migratorConnectionString = fixture.MigratorConnectionString;
        _appConnectionString = fixture.AppConnectionString;
    }

    /// <summary>
    /// Started once per test class, not once per test method — see
    /// <see cref="IdentityRowLevelSecurityTests.Fixture"/>'s doc comment for
    /// the full rationale (Etapa 15A stabilization of Docker daemon load).
    /// </summary>
    public sealed class Fixture : IAsyncLifetime
    {
        private const string AppRolePassword = "test_app_password";
        private const string MigratorRolePassword = "test_migrator_password";

        private PostgreSqlContainer _container = null!;
        public string MigratorConnectionString { get; private set; } = null!;
        public string AppConnectionString { get; private set; } = null!;

        public async Task InitializeAsync()
        {
            _container = new PostgreSqlBuilder()
                .WithImage("postgres:16")
                .WithDatabase("ihostpro_test")
                .WithUsername("ihostpro")
                .WithPassword("ihostpro_dev")
                .Build();

            await _container.StartAsync();

            var adminConnectionString = _container.GetConnectionString();

            await using (var adminConnection = new NpgsqlConnection(adminConnectionString))
            {
                await adminConnection.OpenAsync();
                await ExecuteAsync(adminConnection, $"""
                    CREATE ROLE ihostpro_migrator LOGIN PASSWORD '{MigratorRolePassword}';
                    CREATE ROLE ihostpro_app LOGIN PASSWORD '{AppRolePassword}';
                    GRANT CREATE ON DATABASE ihostpro_test TO ihostpro_migrator;
                    """);
            }

            var builder = new NpgsqlConnectionStringBuilder(adminConnectionString);
            builder.Username = "ihostpro_migrator";
            builder.Password = MigratorRolePassword;
            MigratorConnectionString = builder.ConnectionString;
            builder.Username = "ihostpro_app";
            builder.Password = AppRolePassword;
            AppConnectionString = builder.ConnectionString;

            await using var migratorDbContext = CreateDbContext(MigratorConnectionString, new TenantContext());
            await migratorDbContext.Database.MigrateAsync();
        }

        public async Task DisposeAsync() => await _container.DisposeAsync();
    }

    // ---- Service graph -----------------------------------------------

    private ServiceProvider BuildServices(int maxFailedAccessAttempts = 5, Action<IServiceCollection>? overrides = null)
    {
        // A fresh, unique RSA key per call (never a shared/static one):
        // reusing identical key material across multiple independently
        // disposed RSA instances triggered ObjectDisposedException on
        // RSABCrypt here — Windows' CNG backend appears to share the
        // underlying native handle for content-identical imported keys, so
        // disposing one ConfigurationJwtSigningKeyProvider's RSA (Etapa 6)
        // invalidated a different test's "independent" instance of the same
        // key. Found and fixed while writing these tests, not assumed.
        using var signingKey = RSA.Create(2048);
        var signingKeyPem = signingKey.ExportRSAPrivateKeyPem();

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Identity"] = _appConnectionString,
            ["Identity:Jwt:Issuer"] = "https://identity.ihostpro.test",
            ["Identity:Jwt:Audience"] = "ihostpro-api-test",
            ["Identity:Jwt:AccessTokenLifetime"] = "00:15:00",
            ["Identity:Jwt:ClockSkew"] = "00:01:00",
            ["Identity:Jwt:SigningKey:PrivateKeyPem"] = signingKeyPem,
            ["Identity:AccountLockout:MaxFailedAccessAttempts"] = maxFailedAccessAttempts.ToString(),
            ["Identity:AccountLockout:DefaultLockoutDuration"] = "00:05:00",
            ["Identity:AccountLockout:AllowedForNewUsers"] = "true",
        }).Build();

        var services = new ServiceCollection();
        services.AddScoped<ITenantContext, TenantContext>();
        services.AddScoped<ICurrentTenantProvider, TenantContextCurrentTenantProvider>();
        services.AddIHostProTenantAwarePipeline();
        services.AddIdentityModule(configuration, isDevelopmentEnvironment: false);
        services.AddIdentityJwtIssuance(configuration);
        // LoginCommandHandler stages Integration Events here (Incremento 2
        // plan, Etapa 15) — this test drives it directly through
        // ITenantAwareUnitOfWork, not IIdentityTransactionExecutor, so
        // nothing ever drains/publishes the collector; it only needs to be
        // resolvable for the handler's constructor.
        services.AddScoped<IIntegrationEventCollector, IntegrationEventCollector>();
        services.AddScoped<LoginCommandHandler>();

        overrides?.Invoke(services);

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Manually replicates ValidationBehavior -> TenantBootstrapBehavior -> handler,
    /// exactly as the (not-yet-wired) Mediator dispatch would.
    /// </summary>
    private static async Task<Result<AuthTokensResult>> ExecuteLoginAsync(
        ServiceProvider root, LoginCommand command, CancellationToken cancellationToken = default)
    {
        using var scope = root.CreateScope();
        var sp = scope.ServiceProvider;

        var validation = await new LoginCommandValidator().ValidateAsync(command, cancellationToken);
        if (!validation.IsValid)
        {
            return Result.Failure<AuthTokensResult>(new Error(
                string.Join(",", validation.Errors.Select(e => e.ErrorCode)),
                string.Join("; ", validation.Errors.Select(e => e.ErrorMessage))));
        }

        var tenantId = await sp.GetRequiredService<ITenantBootstrapResolver<LoginCommand>>()
            .ResolveTenantAsync(command, cancellationToken);

        if (tenantId is null)
            return Result.Failure<AuthTokensResult>(new Error("Tenant.NotFound", "The tenant could not be resolved."));

        sp.GetRequiredService<ITenantContext>().SetTenant(tenantId.Value);

        return await sp.GetRequiredService<ITenantAwareUnitOfWork>().ExecuteAsync(
            readOnly: false,
            () => sp.GetRequiredService<LoginCommandHandler>().Handle(command, cancellationToken).AsTask(),
            cancellationToken);
    }

    // ---- Seeding --------------------------------------------------------

    private async Task<Guid> SeedTenantAsync(bool active = true)
    {
        var tenantId = Guid.NewGuid();
        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);

        var tenant = Tenant.Provision(
            tenantId, TenantSlug.Create($"tenant-{Guid.NewGuid():N}"[..20]), "Test Tenant", DateTimeOffset.UtcNow);
        if (!active)
            tenant.Suspend();
        dbContext.Tenants.Add(tenant);

        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();

        return tenant.Id;
    }

    private async Task<(Guid TenantId, Guid UserId, string Email)> SeedTenantWithUserAsync(
        bool blocked = false, string? emailOverride = null)
    {
        var tenantId = await SeedTenantAsync();
        var email = emailOverride ?? $"{Guid.NewGuid():N}@ihostpro.com";

        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);

        var hasher = new IHostPro.Contexts.Identity.Infrastructure.Security.Argon2PasswordHasher(
            new IHostPro.Contexts.Identity.Infrastructure.Security.KonsciousArgon2idPrimitive(),
            Microsoft.Extensions.Options.Options.Create(new IHostPro.Contexts.Identity.Infrastructure.Security.Argon2Options()));
        var hash = PasswordHash.FromEncoded(hasher.HashPassword(null!, KnownPassword));

        var user = User.Register(Guid.NewGuid(), tenantId, Email.Create(email), "Test User", hash, DateTimeOffset.UtcNow);
        if (blocked)
            user.Block(DateTimeOffset.UtcNow);
        dbContext.Users.Add(user);

        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();

        return (tenantId, user.Id, email);
    }

    private async Task AssignRoleAsync(Guid tenantId, Guid userId, string roleCode)
    {
        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);

        dbContext.UserRoles.Add(new UserRole(tenantId, userId, roleCode, DateTimeOffset.UtcNow, assignedByUserId: null));

        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();
    }

    private static LoginCommand LoginAs(string tenantSlug, string email, string password = KnownPassword) =>
        new(tenantSlug, email, password, new AuthenticationRequestContext("203.0.113.7", "iPhone", "Safari"));

    private async Task<string> GetTenantSlugAsync(Guid tenantId)
    {
        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);

        var tenant = await dbContext.Tenants.SingleAsync(t => t.Id == tenantId);
        return tenant.Slug.Value;
    }

    // ---- Tests: happy path -----------------------------------------------

    [Fact]
    public async Task Login_with_correct_credentials_succeeds_and_returns_a_token_pair()
    {
        var (tenantId, _, email) = await SeedTenantWithUserAsync();
        var slug = await GetTenantSlugAsync(tenantId);
        using var services = BuildServices();

        var result = await ExecuteLoginAsync(services, LoginAs(slug, email));

        result.IsSuccess.Should().BeTrue();
        result.Value.AccessToken.Should().NotBeNullOrWhiteSpace();
        result.Value.RefreshToken.Should().NotBeNullOrWhiteSpace();
        result.Value.TokenType.Should().Be("Bearer");
    }

    [Fact]
    public async Task Successful_login_persists_session_refresh_token_and_audit_entry_atomically()
    {
        var (tenantId, userId, email) = await SeedTenantWithUserAsync();
        var slug = await GetTenantSlugAsync(tenantId);
        using var services = BuildServices();

        var result = await ExecuteLoginAsync(services, LoginAs(slug, email));
        result.IsSuccess.Should().BeTrue();

        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);

        var session = await dbContext.Sessions.SingleAsync(s => s.UserId == userId);
        var refreshToken = await dbContext.RefreshTokens.SingleAsync(rt => rt.SessionId == session.Id);
        var auditEntry = await dbContext.SecurityAuditLog.SingleAsync(e => e.SessionId == session.Id);

        session.Status.Should().Be(SessionStatus.Active);
        refreshToken.UserId.Should().Be(userId);
        auditEntry.EventType.Should().Be(SecurityAuditEventType.LoginSucceeded);
        auditEntry.UserId.Should().Be(userId);
        auditEntry.RefreshTokenId.Should().Be(refreshToken.TokenId);
    }

    [Fact]
    public async Task Login_result_includes_every_assigned_role()
    {
        var (tenantId, userId, email) = await SeedTenantWithUserAsync();
        await AssignRoleAsync(tenantId, userId, "ADMIN");
        await AssignRoleAsync(tenantId, userId, "OPERATOR");
        var slug = await GetTenantSlugAsync(tenantId);
        using var services = BuildServices();

        var result = await ExecuteLoginAsync(services, LoginAs(slug, email));

        var jwt = new JsonWebToken(result.Value.AccessToken);
        var roles = jwt.GetPayloadValue<string[]>("role");
        roles.Should().BeEquivalentTo(["ADMIN", "OPERATOR"]);
    }

    [Fact]
    public async Task Login_succeeds_with_no_assigned_roles()
    {
        var (tenantId, _, email) = await SeedTenantWithUserAsync();
        var slug = await GetTenantSlugAsync(tenantId);
        using var services = BuildServices();

        var result = await ExecuteLoginAsync(services, LoginAs(slug, email));

        result.IsSuccess.Should().BeTrue();
        var jwt = new JsonWebToken(result.Value.AccessToken);
        jwt.GetPayloadValue<string[]>("role").Should().BeEmpty();
    }

    [Fact]
    public async Task Successful_login_resets_the_failed_access_count()
    {
        var (tenantId, userId, email) = await SeedTenantWithUserAsync();
        var slug = await GetTenantSlugAsync(tenantId);
        using var services = BuildServices();

        await ExecuteLoginAsync(services, LoginAs(slug, email, "wrong-password"));
        var succeeded = await ExecuteLoginAsync(services, LoginAs(slug, email));
        succeeded.IsSuccess.Should().BeTrue();

        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);
        var user = await dbContext.Users.SingleAsync(u => u.Id == userId);

        user.FailedAccessCount.Should().Be(0);
    }

    // ---- Tests: rejection paths, all sharing the same external error ----

    [Fact]
    public async Task Login_for_an_unresolved_tenant_slug_returns_the_generic_failure()
    {
        using var services = BuildServices();

        var result = await ExecuteLoginAsync(services, LoginAs("no-such-tenant-slug", "someone@example.com"));

        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task Login_for_an_inactive_tenant_returns_the_generic_failure_and_is_indistinguishable_from_unresolved()
    {
        var inactiveTenantId = await SeedTenantAsync(active: false);
        var slug = await GetTenantSlugAsync(inactiveTenantId);
        using var services = BuildServices();

        var forInactive = await ExecuteLoginAsync(services, LoginAs(slug, "someone@example.com"));
        var forUnresolved = await ExecuteLoginAsync(services, LoginAs("totally-unknown-slug", "someone@example.com"));

        forInactive.IsFailure.Should().BeTrue();
        forInactive.Error.Should().Be(forUnresolved.Error);
    }

    [Fact]
    public async Task Login_with_an_unknown_email_returns_the_same_generic_failure_as_a_wrong_password()
    {
        var (tenantId, _, email) = await SeedTenantWithUserAsync();
        var slug = await GetTenantSlugAsync(tenantId);
        using var services = BuildServices();

        var unknownEmail = await ExecuteLoginAsync(services, LoginAs(slug, "no-such-user@ihostpro.com"));
        var wrongPassword = await ExecuteLoginAsync(services, LoginAs(slug, email, "definitely-wrong"));

        unknownEmail.IsFailure.Should().BeTrue();
        unknownEmail.Error.Should().Be(wrongPassword.Error);
    }

    [Fact]
    public async Task Login_for_a_blocked_user_returns_the_generic_failure()
    {
        var (tenantId, _, email) = await SeedTenantWithUserAsync(blocked: true);
        var slug = await GetTenantSlugAsync(tenantId);
        using var services = BuildServices();

        var result = await ExecuteLoginAsync(services, LoginAs(slug, email));

        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task Wrong_password_increments_the_failed_access_count_and_records_audit()
    {
        var (tenantId, userId, email) = await SeedTenantWithUserAsync();
        var slug = await GetTenantSlugAsync(tenantId);
        using var services = BuildServices(maxFailedAccessAttempts: 10);

        var result = await ExecuteLoginAsync(services, LoginAs(slug, email, "wrong-password"));

        result.IsFailure.Should().BeTrue();

        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);

        var user = await dbContext.Users.SingleAsync(u => u.Id == userId);
        user.FailedAccessCount.Should().Be(1);

        var audit = await dbContext.SecurityAuditLog.SingleAsync(e => e.UserId == userId);
        audit.EventType.Should().Be(SecurityAuditEventType.LoginRejected);
        audit.ReasonCode.Should().Be(SecurityAuditReasonCode.InvalidPassword);
    }

    [Fact]
    public async Task Account_locks_after_reaching_the_configured_threshold()
    {
        const int threshold = 3;
        var (tenantId, userId, email) = await SeedTenantWithUserAsync();
        var slug = await GetTenantSlugAsync(tenantId);
        using var services = BuildServices(maxFailedAccessAttempts: threshold);

        for (var i = 0; i < threshold; i++)
            await ExecuteLoginAsync(services, LoginAs(slug, email, "wrong-password"));

        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);
        var user = await dbContext.Users.SingleAsync(u => u.Id == userId);

        user.LockoutEnd.Should().NotBeNull();
        user.LockoutEnd.Should().BeAfter(DateTimeOffset.UtcNow);

        var lockedOutEvent = await dbContext.SecurityAuditLog
            .Where(e => e.UserId == userId && e.EventType == SecurityAuditEventType.AccountLockedOut)
            .ToListAsync();
        lockedOutEvent.Should().HaveCount(1);
    }

    [Fact]
    public async Task Login_against_an_already_locked_account_is_rejected_without_further_incrementing_the_count()
    {
        const int threshold = 2;
        var (tenantId, userId, email) = await SeedTenantWithUserAsync();
        var slug = await GetTenantSlugAsync(tenantId);
        using var services = BuildServices(maxFailedAccessAttempts: threshold);

        for (var i = 0; i < threshold; i++)
            await ExecuteLoginAsync(services, LoginAs(slug, email, "wrong-password"));

        // Even with the CORRECT password, the account is already locked.
        var result = await ExecuteLoginAsync(services, LoginAs(slug, email));
        result.IsFailure.Should().BeTrue();

        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);
        var user = await dbContext.Users.SingleAsync(u => u.Id == userId);

        // Not "threshold" — ASP.NET Core Identity's own UserManager.AccessFailedAsync
        // resets FailedAccessCount to 0 the moment it triggers the lockout
        // (LockoutEnd becomes the sole authoritative "is locked" signal from
        // then on, so a fresh attempt count is available once the lockout
        // expires). Verified against the real framework behavior here rather
        // than assumed — the third (rejected-while-locked) attempt must not
        // touch the counter at all either way.
        user.FailedAccessCount.Should().Be(0);
        user.LockoutEnd.Should().NotBeNull();

        var accountLockedEntries = await dbContext.SecurityAuditLog
            .Where(e => e.UserId == userId && e.ReasonCode == SecurityAuditReasonCode.AccountLocked)
            .ToListAsync();
        accountLockedEntries.Should().HaveCount(1);
    }

    [Fact]
    public async Task Failed_login_persists_no_session_or_refresh_token()
    {
        var (tenantId, userId, email) = await SeedTenantWithUserAsync();
        var slug = await GetTenantSlugAsync(tenantId);
        using var services = BuildServices();

        await ExecuteLoginAsync(services, LoginAs(slug, email, "wrong-password"));

        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);

        (await dbContext.Sessions.CountAsync(s => s.UserId == userId)).Should().Be(0);
        (await dbContext.RefreshTokens.CountAsync(rt => rt.UserId == userId)).Should().Be(0);
    }

    // ---- Tests: rollback -------------------------------------------------

    [Fact]
    public async Task A_failure_after_session_and_refresh_token_were_staged_rolls_back_the_entire_transaction()
    {
        var (tenantId, userId, email) = await SeedTenantWithUserAsync();
        var slug = await GetTenantSlugAsync(tenantId);

        using var services = BuildServices(overrides: sc =>
            sc.AddScoped<IJwtTokenGenerator, ThrowingJwtTokenGenerator>());

        var act = async () => await ExecuteLoginAsync(services, LoginAs(slug, email));

        await act.Should().ThrowAsync<InvalidOperationException>();

        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);

        // Session/RefreshToken were staged before the thrown exception, and
        // User.RecordSuccessfulLogin/ResetAccessFailedCount already mutated
        // the tracked entity — none of it must have reached the database.
        (await dbContext.Sessions.CountAsync(s => s.UserId == userId)).Should().Be(0);
        (await dbContext.RefreshTokens.CountAsync(rt => rt.UserId == userId)).Should().Be(0);
        (await dbContext.SecurityAuditLog.CountAsync(e => e.UserId == userId)).Should().Be(0);
    }

    private sealed class ThrowingJwtTokenGenerator : IJwtTokenGenerator
    {
        public JwtAccessTokenResult GenerateAccessToken(JwtAccessTokenRequest request) =>
            throw new InvalidOperationException("Simulated failure after Session/RefreshToken were staged.");
    }

    // ---- Tests: tenant isolation ------------------------------------------

    [Fact]
    public async Task Two_tenants_with_a_user_sharing_the_same_email_never_cross_authenticate()
    {
        const string sharedEmail = "shared@example.com";
        var (tenantAId, _, _) = await SeedTenantWithUserAsync(emailOverride: sharedEmail);
        var (tenantBId, _, _) = await SeedTenantWithUserAsync(emailOverride: sharedEmail);
        var slugA = await GetTenantSlugAsync(tenantAId);
        var slugB = await GetTenantSlugAsync(tenantBId);
        using var services = BuildServices();

        var loginA = await ExecuteLoginAsync(services, LoginAs(slugA, sharedEmail));
        var loginB = await ExecuteLoginAsync(services, LoginAs(slugB, sharedEmail));

        loginA.IsSuccess.Should().BeTrue();
        loginB.IsSuccess.Should().BeTrue();

        var jwtA = new JsonWebToken(loginA.Value.AccessToken);
        var jwtB = new JsonWebToken(loginB.Value.AccessToken);
        jwtA.GetClaim("tenant_id").Value.Should().Be(tenantAId.ToString());
        jwtB.GetClaim("tenant_id").Value.Should().Be(tenantBId.ToString());
        jwtA.Subject.Should().NotBe(jwtB.Subject); // different User rows despite the identical e-mail
    }

    // ---- Tests: no sensitive data leakage --------------------------------

    [Fact]
    public async Task No_rejection_reveals_the_submitted_password_anywhere_in_the_result()
    {
        var (tenantId, _, email) = await SeedTenantWithUserAsync();
        var slug = await GetTenantSlugAsync(tenantId);
        const string password = "this-must-never-leak-into-any-error";
        using var services = BuildServices();

        var result = await ExecuteLoginAsync(services, LoginAs(slug, email, password));

        result.Error.Code.Should().NotContain(password);
        result.Error.Message.Should().NotContain(password);
    }

    [Fact]
    public async Task Audit_log_never_stores_the_password_access_token_refresh_token_or_hash()
    {
        var (tenantId, userId, email) = await SeedTenantWithUserAsync();
        var slug = await GetTenantSlugAsync(tenantId);
        using var services = BuildServices();

        var result = await ExecuteLoginAsync(services, LoginAs(slug, email));
        result.IsSuccess.Should().BeTrue();

        // security_audit_log has no column capable of holding any of these —
        // asserted here by construction/schema, not by inspecting free text.
        var columns = typeof(SecurityAuditEntry).GetProperties().Select(p => p.Name);
        columns.Should().NotContain(["Password", "AccessToken", "RefreshToken", "TokenHash"]);

        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);
        var refreshToken = await dbContext.RefreshTokens.SingleAsync(rt => rt.UserId == userId);

        refreshToken.TokenHash.Should().NotBe(result.Value.RefreshToken); // never the plaintext token
        refreshToken.TokenHash.Should().MatchRegex("^[0-9a-f]{64}$");
    }

    // ---- Helpers ----------------------------------------------------------

    private static async Task SetPostgresTenantAsync(IdentityDbContext dbContext, Guid tenantId) =>
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.tenant_id', {tenantId.ToString()}, true)");

    private IdentityDbContext CreateMigratorDbContextWithTenant(Guid tenantId)
    {
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);
        return CreateDbContext(_migratorConnectionString, tenantContext);
    }

    private static IdentityDbContext CreateDbContext(string connectionString, ITenantContext tenantContext)
    {
        var options = new DbContextOptionsBuilder<IdentityDbContext>()
            .UseNpgsql(connectionString, npgsqlOptions =>
                npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "identity"))
            .Options;

        return new IdentityDbContext(options, tenantContext);
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
