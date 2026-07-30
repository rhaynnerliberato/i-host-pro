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
/// End-to-end test of the three self-service HTTP endpoints (Incremento 3,
/// Checkpoint 4) — <see cref="UsersController"/> — against the REAL
/// composition root wiring, exactly mirroring <see cref="AuthEndpointsTests"/>'s
/// pattern (real host, real JWT, real PostgreSQL and Redis, no synthetic
/// endpoint): every request goes through the controller -&gt; <c>ISender</c> -&gt;
/// the real generated Mediator dispatch, including — for
/// <c>DELETE /users/me/sessions/{sessionId}</c> — the real outbox and, when
/// Redis is reachable, the real post-commit session-revocation cache write
/// that <see cref="ConfigureJwtBearerOptions"/> consults on every subsequent
/// authenticated request.
/// </summary>
public class UsersEndpointsTests : IClassFixture<UsersEndpointsTests.Fixture>
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

    public UsersEndpointsTests(Fixture fixture)
    {
        _redisContainer = fixture.RedisContainer;
        _migratorConnectionString = fixture.MigratorConnectionString;
        _appConnectionString = fixture.AppConnectionString;

        // Deliberately NOT shared via Fixture — see JwtBearerAuthenticationTests'
        // constructor doc comment for the full Windows CNG native-handle-sharing
        // rationale (Etapa 15A stabilization).
        using var signingKey = RSA.Create(2048);
        _signingKeyPem = signingKey.ExportRSAPrivateKeyPem();
    }

    /// <summary>
    /// Started once per test class, not once per test method — see
    /// <see cref="IdentityRowLevelSecurityTests.Fixture"/>'s doc comment for
    /// the full rationale. Also provisions Identity's outbox, mirroring
    /// <see cref="AuthEndpointsTests.Fixture"/>.
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
                    services.AddControllers().AddApplicationPart(typeof(UsersController).Assembly);
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

    private async Task<Guid> GetSoleSessionIdAsync(Guid tenantId, Guid userId)
    {
        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);

        var session = await dbContext.Sessions.SingleAsync(s => s.UserId == userId);
        return session.Id;
    }

    private async Task<Guid> SeedExtraSessionAsync(Guid tenantId, Guid userId)
    {
        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);

        var session = Session.Open(
            Guid.NewGuid(), tenantId, userId, DateTimeOffset.UtcNow.AddMinutes(-5), "Android", "Chrome", "203.0.113.9");
        dbContext.Sessions.Add(session);

        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();

        return session.Id;
    }

    private static async Task<AuthTokensResponse> LoginAsync(HttpClient client, string tenantSlug, string email, string password = KnownPassword)
    {
        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(tenantSlug, email, password));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AuthTokensResponse>(JsonWebDefaults))!;
    }

    private static Task<HttpResponseMessage> GetAsync(HttpClient client, string route, string? token) =>
        SendAsync(client, HttpMethod.Get, route, token);

    private static Task<HttpResponseMessage> DeleteAsync(HttpClient client, string route, string? token) =>
        SendAsync(client, HttpMethod.Delete, route, token);

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string route, string? token)
    {
        var request = new HttpRequestMessage(method, route);
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return await client.SendAsync(request);
    }

    // ---- Tests: 401 without a token -------------------------------------

    [Fact]
    public async Task GetOwnProfile_without_a_token_returns_401()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();

        var response = await GetAsync(client, "/api/v1/users/me", token: null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ListOwnSessions_without_a_token_returns_401()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();

        var response = await GetAsync(client, "/api/v1/users/me/sessions", token: null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task RevokeOwnSession_without_a_token_returns_401()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();

        var response = await DeleteAsync(client, $"/api/v1/users/me/sessions/{Guid.NewGuid()}", token: null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ---- Tests: profile ---------------------------------------------------

    [Fact]
    public async Task GetOwnProfile_returns_200_with_the_callers_own_data_and_no_store_header()
    {
        var tenantId = await SeedTenantAsync();
        var (userId, email) = await SeedUserAsync(tenantId);
        var slug = await GetTenantSlugAsync(tenantId);
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var login = await LoginAsync(client, slug, email);

        var response = await GetAsync(client, "/api/v1/users/me", login.AccessToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        var body = await response.Content.ReadFromJsonAsync<OwnProfileResponse>(JsonWebDefaults);
        body!.Id.Should().Be(userId);
        body.Email.Should().Be(email);
        var raw = await response.Content.ReadAsStringAsync();
        raw.Should().NotContain("passwordHash", "sensitive internal fields must never reach the wire");
    }

    [Fact]
    public async Task GetOwnProfile_ignores_a_client_supplied_user_id_query_parameter()
    {
        var tenantId = await SeedTenantAsync();
        var (userId, email) = await SeedUserAsync(tenantId);
        var (impersonatedUserId, _) = await SeedUserAsync(tenantId);
        var slug = await GetTenantSlugAsync(tenantId);
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var login = await LoginAsync(client, slug, email);

        var response = await GetAsync(client, $"/api/v1/users/me?userId={impersonatedUserId}", login.AccessToken);

        var body = await response.Content.ReadFromJsonAsync<OwnProfileResponse>(JsonWebDefaults);
        body!.Id.Should().Be(userId); // never the query-string-supplied id
    }

    // ---- Tests: sessions --------------------------------------------------

    [Fact]
    public async Task ListOwnSessions_returns_200_and_correctly_identifies_the_current_session()
    {
        var tenantId = await SeedTenantAsync();
        var (userId, email) = await SeedUserAsync(tenantId);
        var slug = await GetTenantSlugAsync(tenantId);
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var login = await LoginAsync(client, slug, email);
        var currentSessionId = await GetSoleSessionIdAsync(tenantId, userId);
        var otherSessionId = await SeedExtraSessionAsync(tenantId, userId);

        var response = await GetAsync(client, "/api/v1/users/me/sessions", login.AccessToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        var body = await response.Content.ReadFromJsonAsync<List<OwnSessionResponse>>(JsonWebDefaults);
        body!.Select(s => s.SessionId).Should().BeEquivalentTo([currentSessionId, otherSessionId]);
        body.Should().ContainSingle(s => s.IsCurrent).Which.SessionId.Should().Be(currentSessionId);
        body.Single(s => s.SessionId == otherSessionId).IsCurrent.Should().BeFalse();
    }

    // ---- Tests: revocation --------------------------------------------------

    [Fact]
    public async Task RevokeOwnSession_for_a_valid_own_session_returns_204()
    {
        var tenantId = await SeedTenantAsync();
        var (userId, email) = await SeedUserAsync(tenantId);
        var slug = await GetTenantSlugAsync(tenantId);
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var login = await LoginAsync(client, slug, email);
        var otherSessionId = await SeedExtraSessionAsync(tenantId, userId);

        var response = await DeleteAsync(client, $"/api/v1/users/me/sessions/{otherSessionId}", login.AccessToken);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await response.Content.ReadAsByteArrayAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task RevokeOwnSession_for_a_session_belonging_to_a_different_user_returns_404()
    {
        var tenantId = await SeedTenantAsync();
        var (_, email) = await SeedUserAsync(tenantId);
        var (otherUserId, _) = await SeedUserAsync(tenantId);
        var slug = await GetTenantSlugAsync(tenantId);
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var login = await LoginAsync(client, slug, email);
        var otherUsersSessionId = await SeedExtraSessionAsync(tenantId, otherUserId);

        var response = await DeleteAsync(client, $"/api/v1/users/me/sessions/{otherUsersSessionId}", login.AccessToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task RevokeOwnSession_for_a_session_belonging_to_a_different_tenant_returns_404()
    {
        var tenantA = await SeedTenantAsync();
        var tenantB = await SeedTenantAsync();
        var (userIdInTenantA, emailInTenantA) = await SeedUserAsync(tenantA);
        var (userIdInTenantB, _) = await SeedUserAsync(tenantB);
        var slugA = await GetTenantSlugAsync(tenantA);
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var login = await LoginAsync(client, slugA, emailInTenantA);
        var sessionInTenantB = await SeedExtraSessionAsync(tenantB, userIdInTenantB);

        var response = await DeleteAsync(client, $"/api/v1/users/me/sessions/{sessionInTenantB}", login.AccessToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task RevokeOwnSession_for_an_already_revoked_session_returns_404()
    {
        var tenantId = await SeedTenantAsync();
        var (userId, email) = await SeedUserAsync(tenantId);
        var slug = await GetTenantSlugAsync(tenantId);
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var login = await LoginAsync(client, slug, email);
        var otherSessionId = await SeedExtraSessionAsync(tenantId, userId);
        var first = await DeleteAsync(client, $"/api/v1/users/me/sessions/{otherSessionId}", login.AccessToken);
        first.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var second = await DeleteAsync(client, $"/api/v1/users/me/sessions/{otherSessionId}", login.AccessToken);

        second.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task After_revoking_the_current_session_a_subsequent_request_with_the_same_access_token_is_rejected()
    {
        var tenantId = await SeedTenantAsync();
        var (userId, email) = await SeedUserAsync(tenantId);
        var slug = await GetTenantSlugAsync(tenantId);
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var login = await LoginAsync(client, slug, email);
        var currentSessionId = await GetSoleSessionIdAsync(tenantId, userId);

        var revokeResponse = await DeleteAsync(client, $"/api/v1/users/me/sessions/{currentSessionId}", login.AccessToken);
        revokeResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var followUp = await GetAsync(client, "/api/v1/users/me", login.AccessToken);

        followUp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Revoking_a_different_own_session_does_not_invalidate_the_current_session()
    {
        var tenantId = await SeedTenantAsync();
        var (userId, email) = await SeedUserAsync(tenantId);
        var slug = await GetTenantSlugAsync(tenantId);
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var login = await LoginAsync(client, slug, email);
        var otherSessionId = await SeedExtraSessionAsync(tenantId, userId);

        var revokeResponse = await DeleteAsync(client, $"/api/v1/users/me/sessions/{otherSessionId}", login.AccessToken);
        revokeResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var followUp = await GetAsync(client, "/api/v1/users/me", login.AccessToken);

        followUp.StatusCode.Should().Be(HttpStatusCode.OK);
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
