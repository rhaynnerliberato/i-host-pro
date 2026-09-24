using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Infrastructure.Messaging;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.BuildingBlocks.Infrastructure.Persistence;
using IHostPro.Contexts.Identity.Api.Authorization;
using IHostPro.Contexts.Identity.Api.Contracts;
using IHostPro.Contexts.Identity.Api.Controllers;
using IHostPro.Contexts.Identity.Application;
using IHostPro.Contexts.Identity.Domain;
using IHostPro.Contexts.Identity.Domain.ValueObjects;
using IHostPro.Contexts.Identity.Infrastructure;
using IHostPro.Contexts.Identity.Infrastructure.Authentication;
using IHostPro.Contexts.Identity.Infrastructure.Caching;
using IHostPro.Contexts.Identity.Infrastructure.Persistence;
using IHostPro.Contexts.Identity.Infrastructure.Security;
using JasperFx;
using JasperFx.Resources;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Npgsql;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;
using Wolverine;
using Wolverine.EntityFrameworkCore;

namespace IHostPro.Contexts.Identity.Tests.Integration;

/// <summary>
/// End-to-end proof of the Tenant Suspension/Reactivation Enforcement
/// workstream against the REAL composition root wiring — same shape as
/// <see cref="UserBlockingEndpointsTests"/>'s "effect on authentication"
/// tests (Section 9), except there is no HTTP admin endpoint for this
/// operation (the administrative surface is the console tool,
/// <c>tools/IHostPro.TenantProvisioning</c> — Tenant is not RLS-protected and
/// this workstream deliberately did not add a platform-admin HTTP
/// authorization model). Suspend/Reactivate are therefore performed exactly
/// the way <c>TenantProvisioner.SuspendAsync</c>/<c>ReactivateAsync</c> do it
/// (fetch the real <see cref="Tenant"/> aggregate, call
/// <see cref="Tenant.Suspend"/>/<see cref="Tenant.Reactivate"/>, then write
/// the corresponding <see cref="ITenantAccessStateCache"/> entry), proving
/// the real <c>ConfigureJwtBearerOptions.OnTokenValidatedAsync</c>
/// enforcement point reacts correctly — not a hand-chained handler call.
/// </summary>
public class TenantSuspensionEnforcementTests : IClassFixture<TenantSuspensionEnforcementTests.Fixture>
{
    private const string OutboxSchema = "identity_messaging";
    private const string KnownPassword = "Correct-Horse-Battery-Staple-42!";
    private const string Issuer = "https://identity.ihostpro.test";
    private const string Audience = "ihostpro-api-test";

    private static readonly JsonSerializerOptions JsonWebDefaults = new(JsonSerializerDefaults.Web);

    private readonly RedisContainer _redisContainer;
    private readonly string _migratorConnectionString;
    private readonly string _appConnectionString;
    private readonly string _signingKeyPem;

    public TenantSuspensionEnforcementTests(Fixture fixture)
    {
        _redisContainer = fixture.RedisContainer;
        _migratorConnectionString = fixture.MigratorConnectionString;
        _appConnectionString = fixture.AppConnectionString;

        // Deliberately NOT shared via Fixture — see JwtBearerAuthenticationTests'
        // constructor doc comment for the full Windows CNG native-handle-sharing
        // rationale.
        using var signingKey = RSA.Create(2048);
        _signingKeyPem = signingKey.ExportRSAPrivateKeyPem();
    }

    public sealed class Fixture : IAsyncLifetime
    {
        private const string AppRolePassword = "test_app_password";
        private const string MigratorRolePassword = "test_migrator_password";

        private PostgreSqlContainer _postgresContainer = null!;
        public RedisContainer RedisContainer { get; private set; } = null!;
        public string MigratorConnectionString { get; private set; } = null!;
        public string AppConnectionString { get; private set; } = null!;

        public async Task InitializeAsync()
        {
            _postgresContainer = new PostgreSqlBuilder()
                .WithImage("postgres:16")
                .WithDatabase("ihostpro_test")
                .WithUsername("ihostpro")
                .WithPassword("ihostpro_dev")
                .Build();
            RedisContainer = new RedisBuilder().WithImage("redis:7-alpine").Build();

            await Task.WhenAll(_postgresContainer.StartAsync(), RedisContainer.StartAsync());

            var adminConnectionString = _postgresContainer.GetConnectionString();

            await using (var adminConnection = new NpgsqlConnection(adminConnectionString))
            {
                await adminConnection.OpenAsync();
                await using var command = adminConnection.CreateCommand();
                command.CommandText = $"""
                    CREATE ROLE ihostpro_migrator LOGIN PASSWORD '{MigratorRolePassword}';
                    CREATE ROLE ihostpro_app LOGIN PASSWORD '{AppRolePassword}';
                    GRANT CREATE ON DATABASE ihostpro_test TO ihostpro_migrator;
                    """;
                await command.ExecuteNonQueryAsync();
            }

            var builder = new NpgsqlConnectionStringBuilder(adminConnectionString) { Username = "ihostpro_migrator", Password = MigratorRolePassword };
            MigratorConnectionString = builder.ConnectionString;
            builder.Username = "ihostpro_app";
            builder.Password = AppRolePassword;
            AppConnectionString = builder.ConnectionString;

            await using var migratorDbContext = CreateDbContext(MigratorConnectionString, new TenantContext());
            await migratorDbContext.Database.MigrateAsync();

            await ProvisionOutboxAsMigratorAsync();
        }

        public async Task DisposeAsync()
        {
            await _postgresContainer.DisposeAsync();
            await RedisContainer.DisposeAsync();
        }

        private async Task ProvisionOutboxAsMigratorAsync()
        {
            var hostBuilder = Host.CreateApplicationBuilder();
            hostBuilder.UseWolverine(opts =>
            {
                opts.EnrollAncillaryPostgresqlOutbox(MigratorConnectionString, OutboxSchema, typeof(IdentityDbContext));
                opts.AutoBuildMessageStorageOnStartup = AutoCreate.None;
                opts.UseEntityFrameworkCoreTransactions();
            });

            using var outboxHost = hostBuilder.Build();
            await outboxHost.SetupResources();

            await using var connection = new NpgsqlConnection(MigratorConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                GRANT USAGE ON SCHEMA {OutboxSchema} TO ihostpro_app;
                GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA {OutboxSchema} TO ihostpro_app;
                GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA {OutboxSchema} TO ihostpro_app;
                ALTER DEFAULT PRIVILEGES FOR ROLE ihostpro_migrator IN SCHEMA {OutboxSchema}
                  GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO ihostpro_app;
                ALTER DEFAULT PRIVILEGES FOR ROLE ihostpro_migrator IN SCHEMA {OutboxSchema}
                  GRANT USAGE, SELECT ON SEQUENCES TO ihostpro_app;
                """;
            await command.ExecuteNonQueryAsync();
        }
    }

    // ---- Server ------------------------------------------------------

    private async Task<IHost> BuildHostAsync()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Identity"] = _appConnectionString,
            ["Identity:Jwt:Issuer"] = Issuer,
            ["Identity:Jwt:Audience"] = Audience,
            ["Identity:Jwt:AccessTokenLifetime"] = "00:15:00",
            ["Identity:Jwt:ClockSkew"] = "00:01:00",
            ["Identity:Jwt:SigningKey:PrivateKeyPem"] = _signingKeyPem,
            ["Identity:AccountLockout:MaxFailedAccessAttempts"] = "5",
            ["Identity:AccountLockout:DefaultLockoutDuration"] = "00:05:00",
            ["Identity:AccountLockout:AllowedForNewUsers"] = "true",
            ["Identity:RefreshToken:Lifetime"] = "30.00:00:00",
            ["Identity:RefreshToken:SecretSizeBytes"] = "32",
            ["Identity:RefreshToken:ConcurrentRotationGraceWindow"] = "00:00:10",
            ["Identity:SessionRevocationCache:ConnectionString"] = _redisContainer.GetConnectionString(),
        }).Build();

        var hostBuilder = new HostBuilder()
            .ConfigureWebHost(webHost =>
            {
                webHost.UseTestServer();
                webHost.ConfigureAppConfiguration(cfg => cfg.AddConfiguration(configuration));
                webHost.ConfigureServices(services =>
                {
                    services.AddControllers().AddApplicationPart(typeof(UserAdministrationController).Assembly);
                    services.AddScoped<ITenantContext, TenantContext>();
                    services.AddScoped<ICurrentTenantProvider, TenantContextCurrentTenantProvider>();
                    services.AddIHostProTenantAwarePipeline();
                    services.AddIdentityModule(configuration, isDevelopmentEnvironment: false);
                    services.AddIdentityJwtIssuance(configuration);
                    services.AddIdentitySessionRevocationCache(configuration);
                    // The one addition versus UserBlockingEndpointsTests'
                    // otherwise-identical BuildHostAsync — must come after
                    // AddIdentitySessionRevocationCache, which owns the
                    // shared IConnectionMultiplexer this reuses.
                    services.AddIdentityTenantAccessStateCache();
                    services.AddIdentityJwtBearerAuthentication();
                    services.AddIdentityAuthorization();
                    services.AddSingleton<ITransactionalEmailSender, ThrowingTransactionalEmailSender>();
                    services.AddIdentityCommandDispatch(configuration);
                });
                webHost.Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints => endpoints.MapControllers());
                });
            })
            .UseWolverine(opts =>
            {
                opts.EnrollAncillaryPostgresqlOutbox(_appConnectionString, OutboxSchema, typeof(IdentityDbContext));
                opts.AutoBuildMessageStorageOnStartup = AutoCreate.None;
                opts.UseEntityFrameworkCoreTransactions();
            });

        return await hostBuilder.StartAsync();
    }

    // ---- Seeding --------------------------------------------------------

    private async Task<(Guid TenantId, string Slug)> SeedTenantAsync()
    {
        var tenantId = Guid.NewGuid();
        var slug = $"tenant-{Guid.NewGuid():N}"[..20];
        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);

        var tenant = Tenant.Provision(tenantId, TenantSlug.Create(slug), "Test Tenant", DateTimeOffset.UtcNow);
        dbContext.Tenants.Add(tenant);

        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();

        return (tenant.Id, slug);
    }

    private async Task<(Guid UserId, string Email)> SeedUserAsync(Guid tenantId)
    {
        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);

        var hasher = new Argon2PasswordHasher(new KonsciousArgon2idPrimitive(), Options.Create(new Argon2Options()));
        var hash = PasswordHash.FromEncoded(hasher.HashPassword(null!, KnownPassword));
        var email = $"{Guid.NewGuid():N}@ihostpro.com";
        var user = User.Register(Guid.NewGuid(), tenantId, Email.Create(email), "Test User", hash, DateTimeOffset.UtcNow);
        dbContext.Users.Add(user);

        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();

        return (user.Id, email);
    }

    private async Task<Guid> SeedSessionAsync(Guid tenantId, Guid userId)
    {
        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);

        var session = Session.Open(Guid.NewGuid(), tenantId, userId, DateTimeOffset.UtcNow, "iPhone", "Safari", "203.0.113.7");
        dbContext.Sessions.Add(session);

        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();

        return session.Id;
    }

    // ---- Tenant lifecycle: exactly what TenantProvisioner.SuspendAsync/
    // ReactivateAsync do, with no HTTP admin endpoint to call instead ------

    private async Task SuspendTenantAsync(IHost host, Guid tenantId)
    {
        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        var tenant = await dbContext.Tenants.SingleAsync(t => t.Id == tenantId);
        tenant.Suspend();
        await dbContext.SaveChangesAsync();

        using var scope = host.Services.CreateScope();
        var accessStateCache = scope.ServiceProvider.GetRequiredService<ITenantAccessStateCache>();
        await accessStateCache.MarkSuspendedAsync(tenantId, CancellationToken.None);
    }

    private async Task<DateTimeOffset> ReactivateTenantAsync(IHost host, Guid tenantId)
    {
        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        var tenant = await dbContext.Tenants.SingleAsync(t => t.Id == tenantId);
        tenant.Reactivate();
        await dbContext.SaveChangesAsync();

        var reactivatedAtUtc = DateTimeOffset.UtcNow;
        using var scope = host.Services.CreateScope();
        var accessStateCache = scope.ServiceProvider.GetRequiredService<ITenantAccessStateCache>();
        await accessStateCache.MarkReactivatedAsync(tenantId, reactivatedAtUtc, CancellationToken.None);

        return reactivatedAtUtc;
    }

    private static async Task<string> GenerateTokenAsync(IHost host, Guid userId, Guid tenantId, Guid sessionId, string[] roles)
    {
        using var scope = host.Services.CreateScope();
        var generator = scope.ServiceProvider.GetRequiredService<IJwtTokenGenerator>();

        var request = new JwtAccessTokenRequest(UserId: userId, TenantId: tenantId, SessionId: sessionId, Roles: roles);

        return generator.GenerateAccessToken(request).Token;
    }

    private static Task<HttpResponseMessage> GetAsync(HttpClient client, string route, string? token) =>
        SendAsync(client, HttpMethod.Get, route, token);

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string route, string? token)
    {
        var request = new HttpRequestMessage(method, route);
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return await client.SendAsync(request);
    }

    private static async Task<AuthTokensResponse> LoginAsync(HttpClient client, string tenantSlug, string email, string password = KnownPassword)
    {
        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(tenantSlug, email, password));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AuthTokensResponse>(JsonWebDefaults))!;
    }

    // ---- Tests ---------------------------------------------------------

    [Fact]
    public async Task Suspending_a_tenant_rejects_its_already_issued_access_token()
    {
        var (tenantId, _) = await SeedTenantAsync();
        var (userId, _) = await SeedUserAsync(tenantId);
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var sessionId = await SeedSessionAsync(tenantId, userId);
        var token = await GenerateTokenAsync(host, userId, tenantId, sessionId, ["HOUSEKEEPER"]);
        (await GetAsync(client, "/api/v1/users/me", token)).StatusCode.Should().Be(HttpStatusCode.OK, "the token is valid before suspension");

        await SuspendTenantAsync(host, tenantId);

        var response = await GetAsync(client, "/api/v1/users/me", token);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "suspension must block an already-issued token immediately, not merely at its natural expiry");
    }

    [Fact]
    public async Task After_reactivation_a_fresh_login_works_but_the_pre_reactivation_token_remains_rejected()
    {
        var (tenantId, slug) = await SeedTenantAsync();
        var (userId, email) = await SeedUserAsync(tenantId);
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var sessionId = await SeedSessionAsync(tenantId, userId);
        var preSuspensionToken = await GenerateTokenAsync(host, userId, tenantId, sessionId, ["HOUSEKEEPER"]);
        await SuspendTenantAsync(host, tenantId);
        (await GetAsync(client, "/api/v1/users/me", preSuspensionToken)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // The access token's iat and ITenantAccessStateCache's cutover are
        // both wall-clock-second granularity (a JWT NumericDate has no
        // sub-second precision) — a real gap is needed here so the two
        // events land in different seconds, exactly as they always would in
        // real operation (an admin action takes far longer than one second),
        // never coincidentally in the same one as this fast in-process test
        // could otherwise produce.
        await Task.Delay(TimeSpan.FromSeconds(1.5));
        await ReactivateTenantAsync(host, tenantId);

        var oldTokenFollowUp = await GetAsync(client, "/api/v1/users/me", preSuspensionToken);
        oldTokenFollowUp.StatusCode.Should().Be(
            HttpStatusCode.Unauthorized, "a token issued before reactivation must not be silently resurrected — a fresh login is required");

        var login = await LoginAsync(client, slug, email);
        var freshLoginResponse = await GetAsync(client, "/api/v1/users/me", login.AccessToken);
        freshLoginResponse.StatusCode.Should().Be(HttpStatusCode.OK, "reactivation must restore normal access for a fresh login");
    }

    [Fact]
    public async Task Suspending_one_tenant_does_not_affect_a_different_tenants_token()
    {
        var (suspendedTenantId, _) = await SeedTenantAsync();
        var (unaffectedTenantId, _) = await SeedTenantAsync();
        var (unaffectedUserId, _) = await SeedUserAsync(unaffectedTenantId);
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var unaffectedSessionId = await SeedSessionAsync(unaffectedTenantId, unaffectedUserId);
        var unaffectedToken = await GenerateTokenAsync(host, unaffectedUserId, unaffectedTenantId, unaffectedSessionId, ["HOUSEKEEPER"]);

        await SuspendTenantAsync(host, suspendedTenantId);

        var response = await GetAsync(client, "/api/v1/users/me", unaffectedToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK, "suspending one tenant must never affect a different tenant's access");
    }

    // ---- Plumbing --------------------------------------------------------

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
}
