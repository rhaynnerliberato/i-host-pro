using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.BuildingBlocks.Infrastructure.Persistence;
using IHostPro.Contexts.Identity.Api.Contracts;
using IHostPro.Contexts.Identity.Api.Controllers;
using IHostPro.Contexts.Identity.Domain;
using IHostPro.Contexts.Identity.Domain.ValueObjects;
using IHostPro.Contexts.Identity.Infrastructure;
using IHostPro.Contexts.Identity.Infrastructure.Authentication;
using IHostPro.Contexts.Identity.Infrastructure.Caching;
using IHostPro.Contexts.Identity.Infrastructure.Persistence;
using IHostPro.Contexts.Identity.Infrastructure.Security;
using IHostPro.BuildingBlocks.Infrastructure.Messaging;
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
/// End-to-end test of the three auth HTTP endpoints (Incremento 2 plan,
/// Etapa 14), against the REAL composition root wiring
/// (<c>AddIdentityCommandDispatch</c>/<c>AddIdentityJwtBearerAuthentication</c>/
/// Mediator dispatch/pipeline behaviors/executors) via
/// <c>Microsoft.AspNetCore.TestHost</c> — no test replicates the pipeline by
/// hand here, unlike the earlier handler-level test files; every request
/// goes through <c>AuthController</c> -&gt; <c>ISender</c> -&gt; the real
/// generated Mediator dispatch.
/// </summary>
public class AuthEndpointsTests : IClassFixture<AuthEndpointsTests.Fixture>
{
    private const string OutboxSchema = "identity_messaging";
    private const string KnownPassword = "Correct-Horse-Battery-Staple-42!";
    private const string Issuer = "https://identity.ihostpro.test";
    private const string Audience = "ihostpro-api-test";

    private readonly RedisContainer _redisContainer;
    private readonly string _migratorConnectionString;
    private readonly string _appConnectionString;
    private readonly string _signingKeyPem;

    public AuthEndpointsTests(Fixture fixture)
    {
        _redisContainer = fixture.RedisContainer;
        _migratorConnectionString = fixture.MigratorConnectionString;
        _appConnectionString = fixture.AppConnectionString;

        // Deliberately NOT shared via Fixture, unlike the containers — a
        // fresh key per test method (xUnit creates a new test class instance
        // per [Fact] even under IClassFixture, so the constructor still runs
        // once per test). Confirmed by an exact regression of this during
        // Etapa 15A's fixture-sharing stabilization on JwtBearerAuthenticationTests:
        // sharing one RSA instance's PEM across many independently-built
        // IHost instances (each importing it into its own
        // ConfigurationJwtSigningKeyProvider) reintroduced the documented
        // Windows CNG native-handle-sharing bug (see LoginCommandHandlerTests'
        // BuildServices doc comment).
        using var signingKey = RSA.Create(2048);
        _signingKeyPem = signingKey.ExportRSAPrivateKeyPem();
    }

    /// <summary>
    /// Started once per test class, not once per test method — see
    /// <see cref="IdentityRowLevelSecurityTests.Fixture"/>'s doc comment for
    /// the full rationale (Etapa 15A stabilization of Docker daemon load).
    /// Also provisions Identity's outbox (mirrors <c>IHostPro.MigrationRunner</c>'s
    /// Etapa 15A block) since <c>AddIdentityCommandDispatch</c>'s executors
    /// depend on <see cref="IIdentityTransactionExecutor"/>, which needs
    /// <c>IDbContextOutbox&lt;IdentityDbContext&gt;</c>.
    /// </summary>
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

            await using (var migratorDbContext = CreateDbContext(MigratorConnectionString, new TenantContext()))
            {
                await migratorDbContext.Database.MigrateAsync();
            }

            await ProvisionOutboxAsMigratorAsync();
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

        public async Task DisposeAsync()
        {
            await _postgresContainer.DisposeAsync();
            await RedisContainer.DisposeAsync();
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
                    // Exactly what IHostPro.Api's Program.cs registers for
                    // these concerns — no hand-replicated pipeline chain.
                    services.AddControllers().AddApplicationPart(typeof(AuthController).Assembly);
                    services.AddScoped<ITenantContext, TenantContext>();
                    services.AddScoped<ICurrentTenantProvider, TenantContextCurrentTenantProvider>();
                    services.AddIHostProTenantAwarePipeline();
                    services.AddIdentityModule(configuration, isDevelopmentEnvironment: false);
                    services.AddIdentityJwtIssuance(configuration);
                    services.AddIdentitySessionRevocationCache(configuration);
                    services.AddIdentityJwtBearerAuthentication();
                    services.AddIdentityCommandDispatch();
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

    private async Task<Guid> SeedTenantAsync()
    {
        var tenantId = Guid.NewGuid();
        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);

        var tenant = Tenant.Provision(
            tenantId, TenantSlug.Create($"tenant-{Guid.NewGuid():N}"[..20]), "Test Tenant", DateTimeOffset.UtcNow);
        dbContext.Tenants.Add(tenant);

        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();

        return tenant.Id;
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

    private async Task<string> GetTenantSlugAsync(Guid tenantId)
    {
        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);

        var tenant = await dbContext.Tenants.SingleAsync(t => t.Id == tenantId);
        return tenant.Slug.Value;
    }

    // ---- Tests: happy path --------------------------------------------------

    [Fact]
    public async Task Login_with_correct_credentials_returns_200_with_a_token_pair()
    {
        var tenantId = await SeedTenantAsync();
        var (_, email) = await SeedUserAsync(tenantId);
        var slug = await GetTenantSlugAsync(tenantId);
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();

        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(slug, email, KnownPassword));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<AuthTokensResponse>(JsonWebDefaults);
        body!.AccessToken.Should().NotBeNullOrWhiteSpace();
        body.RefreshToken.Should().NotBeNullOrWhiteSpace();
        body.TokenType.Should().Be("Bearer");
    }

    [Fact]
    public async Task Refresh_with_a_freshly_issued_token_returns_200_with_a_new_token_pair()
    {
        var tenantId = await SeedTenantAsync();
        var (_, email) = await SeedUserAsync(tenantId);
        var slug = await GetTenantSlugAsync(tenantId);
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var login = await LoginAsync(client, slug, email, KnownPassword);

        var response = await client.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(login.RefreshToken));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<AuthTokensResponse>(JsonWebDefaults);
        body!.RefreshToken.Should().NotBe(login.RefreshToken);
    }

    [Fact]
    public async Task Logout_with_a_valid_token_returns_204_with_an_empty_body()
    {
        var tenantId = await SeedTenantAsync();
        var (_, email) = await SeedUserAsync(tenantId);
        var slug = await GetTenantSlugAsync(tenantId);
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var login = await LoginAsync(client, slug, email, KnownPassword);

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/logout");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", login.AccessToken);
        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await response.Content.ReadAsByteArrayAsync()).Should().BeEmpty();
    }

    // ---- Tests: generic authentication failures ------------------------------

    [Fact]
    public async Task Login_with_the_wrong_password_returns_a_generic_401()
    {
        var tenantId = await SeedTenantAsync();
        var (_, email) = await SeedUserAsync(tenantId);
        var slug = await GetTenantSlugAsync(tenantId);
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        const string submittedPassword = "this-is-definitely-wrong";

        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(slug, email, submittedPassword));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotContain(submittedPassword);
    }

    [Fact]
    public async Task Refresh_with_a_garbage_token_returns_the_same_generic_401()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();

        var response = await client.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest("not-a-real-refresh-token"));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Logout_without_a_token_returns_401()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();

        var response = await client.PostAsync("/api/v1/auth/logout", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Logout_with_an_invalid_token_returns_401()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/logout");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-real-jwt");

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ---- Tests: structural validation ---------------------------------------

    [Fact]
    public async Task Login_with_a_missing_email_returns_400_with_stable_codes_and_no_leakage()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        const string submittedPassword = "whatever-the-caller-typed";

        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest("acme", null, submittedPassword));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("email_required");
        body.Should().NotContain(submittedPassword);
    }

    [Fact]
    public async Task Refresh_with_an_empty_token_returns_400_with_stable_codes()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();

        var response = await client.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(string.Empty));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("refresh_token_required");
    }

    // ---- Tests: headers -------------------------------------------------------

    [Fact]
    public async Task Login_response_carries_no_store_and_no_cache_headers()
    {
        var tenantId = await SeedTenantAsync();
        var (_, email) = await SeedUserAsync(tenantId);
        var slug = await GetTenantSlugAsync(tenantId);
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();

        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(slug, email, KnownPassword));

        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        response.Headers.Pragma.ToString().Should().Contain("no-cache");
    }

    [Fact]
    public async Task Refresh_response_carries_no_store_and_no_cache_headers_even_on_failure()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();

        var response = await client.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest("not-a-real-refresh-token"));

        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        response.Headers.Pragma.ToString().Should().Contain("no-cache");
    }

    // ---- Tests: real pipeline/executors on the production path ----------------

    [Fact]
    public async Task Two_concurrent_refresh_requests_over_HTTP_for_the_same_token_converge_to_exactly_one_success()
    {
        // Only passes if the real IRefreshTokenExchangeExecutor (Etapa 10's
        // bounded concurrency retry) is actually on the dispatch path AuthController
        // uses — proving the real executors/pipeline are wired, not bypassed.
        var tenantId = await SeedTenantAsync();
        var (_, email) = await SeedUserAsync(tenantId);
        var slug = await GetTenantSlugAsync(tenantId);
        using var host = await BuildHostAsync();
        using var loginClient = host.GetTestClient();
        var login = await LoginAsync(loginClient, slug, email, KnownPassword);

        // Two independent HttpClients (TestServer.CreateClient()) firing
        // concurrently — a single shared HttpClient against TestServer's
        // in-memory transport was observed to serialize onto the same
        // DbContext instance instead of giving each request its own DI
        // scope, an artifact of the in-memory test transport, not of real
        // Kestrel request handling.
        using var clientA = host.GetTestClient();
        using var clientB = host.GetTestClient();
        var first = clientA.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(login.RefreshToken));
        var second = clientB.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(login.RefreshToken));
        var responses = await Task.WhenAll(first, second);

        responses.Count(r => r.StatusCode == HttpStatusCode.OK).Should().Be(1);
        responses.Count(r => r.StatusCode == HttpStatusCode.Unauthorized).Should().Be(1);
    }

    // ---- Tests: unknown routes --------------------------------------------------

    [Fact]
    public async Task A_route_outside_api_v1_auth_returns_404()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();

        var response = await client.GetAsync("/api/v1/something-else");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ---- Helpers ----------------------------------------------------------

    private static readonly JsonSerializerOptions JsonWebDefaults = new(JsonSerializerDefaults.Web);

    private static async Task<AuthTokensResponse> LoginAsync(HttpClient client, string tenantSlug, string email, string password)
    {
        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(tenantSlug, email, password));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AuthTokensResponse>(JsonWebDefaults))!;
    }

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
