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
/// End-to-end test of the two role-assignment endpoints (Incremento 3,
/// Checkpoint 6) — <c>POST/DELETE /api/v1/users/{userId}/roles(/{roleCode})</c>
/// on <see cref="UserAdministrationController"/> — against the REAL
/// composition root wiring. A separate file from
/// <see cref="UserAdministrationEndpointsTests"/> (Checkpoint 5's Create/List/
/// GetById coverage) deliberately: those three need no Redis; these two do,
/// to prove the session-revocation cascade actually rejects an old access
/// token afterward (mirrors <see cref="UsersEndpointsTests"/>'s equivalent
/// "old token rejected" test for RevokeOwnSession) — rather than add a Redis
/// container to every CP5 test, this file adds its own, isolated Fixture.
/// No RabbitMQ: event CONTENT/routing already covered by
/// <see cref="IdentityIntegrationEventsTests"/>; an unrouted message does not
/// fail the publish/commit itself (same choice as
/// <see cref="UserAdministrationEndpointsTests"/>).
/// </summary>
public class UserRoleAssignmentEndpointsTests : IClassFixture<UserRoleAssignmentEndpointsTests.Fixture>
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

    public UserRoleAssignmentEndpointsTests(Fixture fixture)
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
                    services.AddIdentityJwtBearerAuthentication();
                    services.AddIdentityAuthorization();
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

    private async Task<Guid> SeedUserAsync(Guid tenantId)
    {
        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);

        var hasher = new Argon2PasswordHasher(new KonsciousArgon2idPrimitive(), Options.Create(new Argon2Options()));
        var hash = PasswordHash.FromEncoded(hasher.HashPassword(null!, KnownPassword));
        var user = User.Register(
            Guid.NewGuid(), tenantId, Email.Create($"{Guid.NewGuid():N}@ihostpro.com"), "Test User", hash, DateTimeOffset.UtcNow);
        dbContext.Users.Add(user);

        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();

        return user.Id;
    }

    private async Task SeedUserRoleAsync(Guid tenantId, Guid userId, string roleCode, Guid assignedByUserId)
    {
        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);

        dbContext.UserRoles.Add(new UserRole(tenantId, userId, roleCode, DateTimeOffset.UtcNow, assignedByUserId));

        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();
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

    private static async Task<string> GenerateTokenAsync(IHost host, Guid userId, Guid tenantId, Guid sessionId, string[] roles)
    {
        using var scope = host.Services.CreateScope();
        var generator = scope.ServiceProvider.GetRequiredService<IJwtTokenGenerator>();

        var request = new JwtAccessTokenRequest(UserId: userId, TenantId: tenantId, SessionId: sessionId, Roles: roles);

        return generator.GenerateAccessToken(request).Token;
    }

    private static Task<HttpResponseMessage> GetAsync(HttpClient client, string route, string? token) =>
        SendAsync(client, HttpMethod.Get, route, null, token);

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string route, object? body, string? token) =>
        SendAsync(client, HttpMethod.Post, route, body, token);

    private static Task<HttpResponseMessage> DeleteAsync(HttpClient client, string route, string? token) =>
        SendAsync(client, HttpMethod.Delete, route, null, token);

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string route, object? body, string? token)
    {
        var request = new HttpRequestMessage(method, route);
        if (body is not null)
            request.Content = JsonContent.Create(body, options: JsonWebDefaults);
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return await client.SendAsync(request);
    }

    // ---- Tests: 401 without a token ---------------------------------------

    [Fact]
    public async Task AssignRole_without_a_token_returns_401()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();

        var response = await PostAsync(client, $"/api/v1/users/{Guid.NewGuid()}/roles", new AssignRoleRequest("OPERATOR"), token: null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task RemoveRole_without_a_token_returns_401()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();

        var response = await DeleteAsync(client, $"/api/v1/users/{Guid.NewGuid()}/roles/OPERATOR", token: null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ---- Tests: authenticated but lacking USERS:MANAGE -> 403 -----------------

    [Fact]
    public async Task AssignRole_with_a_role_lacking_USERS_MANAGE_returns_403()
    {
        var tenantId = await SeedTenantAsync();
        var actorId = await SeedUserAsync(tenantId);
        var targetUserId = await SeedUserAsync(tenantId);
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var sessionId = await SeedSessionAsync(tenantId, actorId);
        var token = await GenerateTokenAsync(host, actorId, tenantId, sessionId, ["HOUSEKEEPER"]);

        var response = await PostAsync(client, $"/api/v1/users/{targetUserId}/roles", new AssignRoleRequest("OPERATOR"), token);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ---- Tests: AssignRole ------------------------------------------------

    [Fact]
    public async Task Admin_assigns_a_role_and_receives_204_with_no_store_header()
    {
        var tenantId = await SeedTenantAsync();
        var actorId = await SeedUserAsync(tenantId);
        var targetUserId = await SeedUserAsync(tenantId);
        await SeedUserRoleAsync(tenantId, targetUserId, "HOUSEKEEPER", actorId);
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var actorSessionId = await SeedSessionAsync(tenantId, actorId);
        var token = await GenerateTokenAsync(host, actorId, tenantId, actorSessionId, ["ADMIN"]);

        var response = await PostAsync(client, $"/api/v1/users/{targetUserId}/roles", new AssignRoleRequest("OPERATOR"), token);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
    }

    [Fact]
    public async Task AssignRole_for_a_user_of_a_different_tenant_returns_404()
    {
        var tenantA = await SeedTenantAsync();
        var tenantB = await SeedTenantAsync();
        var actorId = await SeedUserAsync(tenantA);
        var userInTenantB = await SeedUserAsync(tenantB);
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var actorSessionId = await SeedSessionAsync(tenantA, actorId);
        var token = await GenerateTokenAsync(host, actorId, tenantA, actorSessionId, ["ADMIN"]);

        var response = await PostAsync(client, $"/api/v1/users/{userInTenantB}/roles", new AssignRoleRequest("OPERATOR"), token);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task AssignRole_for_a_nonexistent_role_returns_404()
    {
        var tenantId = await SeedTenantAsync();
        var actorId = await SeedUserAsync(tenantId);
        var targetUserId = await SeedUserAsync(tenantId);
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var actorSessionId = await SeedSessionAsync(tenantId, actorId);
        var token = await GenerateTokenAsync(host, actorId, tenantId, actorSessionId, ["ADMIN"]);

        var response = await PostAsync(client, $"/api/v1/users/{targetUserId}/roles", new AssignRoleRequest("NOT_A_REAL_ROLE"), token);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task AssignRole_for_a_role_already_assigned_returns_409()
    {
        var tenantId = await SeedTenantAsync();
        var actorId = await SeedUserAsync(tenantId);
        var targetUserId = await SeedUserAsync(tenantId);
        await SeedUserRoleAsync(tenantId, targetUserId, "OPERATOR", actorId);
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var actorSessionId = await SeedSessionAsync(tenantId, actorId);
        var token = await GenerateTokenAsync(host, actorId, tenantId, actorSessionId, ["ADMIN"]);

        var response = await PostAsync(client, $"/api/v1/users/{targetUserId}/roles", new AssignRoleRequest("OPERATOR"), token);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task AssignRole_request_body_supplied_tenantId_or_actorId_are_ignored()
    {
        var tenantId = await SeedTenantAsync();
        var actorId = await SeedUserAsync(tenantId);
        var targetUserId = await SeedUserAsync(tenantId);
        await SeedUserRoleAsync(tenantId, targetUserId, "HOUSEKEEPER", actorId);
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var actorSessionId = await SeedSessionAsync(tenantId, actorId);
        var token = await GenerateTokenAsync(host, actorId, tenantId, actorSessionId, ["ADMIN"]);
        // AssignRoleRequest only declares RoleCode — extra properties in the
        // raw JSON body (an attempted tenantId/actorId override) simply have
        // no corresponding model property to bind to and are ignored.
        var rawBody = JsonContent.Create(new
        {
            roleCode = "OPERATOR",
            tenantId = Guid.NewGuid(),
            actorId = Guid.NewGuid(),
            assignedBy = Guid.NewGuid(),
        }, options: JsonWebDefaults);
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/users/{targetUserId}/roles") { Content = rawBody };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);
        var userRole = await dbContext.UserRoles.SingleAsync(ur => ur.UserId == targetUserId && ur.RoleCode == "OPERATOR");
        userRole.AssignedByUserId.Should().Be(actorId); // the JWT's own actor, never the body's
    }

    // ---- Tests: RemoveRole --------------------------------------------------

    [Fact]
    public async Task Admin_removes_a_role_and_receives_204_with_no_store_header()
    {
        var tenantId = await SeedTenantAsync();
        var actorId = await SeedUserAsync(tenantId);
        var targetUserId = await SeedUserAsync(tenantId);
        await SeedUserRoleAsync(tenantId, targetUserId, "OPERATOR", actorId);
        await SeedUserRoleAsync(tenantId, targetUserId, "HOUSEKEEPER", actorId);
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var actorSessionId = await SeedSessionAsync(tenantId, actorId);
        var token = await GenerateTokenAsync(host, actorId, tenantId, actorSessionId, ["ADMIN"]);

        var response = await DeleteAsync(client, $"/api/v1/users/{targetUserId}/roles/HOUSEKEEPER", token);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
    }

    [Fact]
    public async Task RemoveRole_for_a_role_not_assigned_returns_409()
    {
        var tenantId = await SeedTenantAsync();
        var actorId = await SeedUserAsync(tenantId);
        var targetUserId = await SeedUserAsync(tenantId);
        await SeedUserRoleAsync(tenantId, targetUserId, "OPERATOR", actorId);
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var actorSessionId = await SeedSessionAsync(tenantId, actorId);
        var token = await GenerateTokenAsync(host, actorId, tenantId, actorSessionId, ["ADMIN"]);

        var response = await DeleteAsync(client, $"/api/v1/users/{targetUserId}/roles/HOUSEKEEPER", token);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task RemoveRole_of_the_users_only_role_returns_409()
    {
        var tenantId = await SeedTenantAsync();
        var actorId = await SeedUserAsync(tenantId);
        var targetUserId = await SeedUserAsync(tenantId);
        await SeedUserRoleAsync(tenantId, targetUserId, "OPERATOR", actorId);
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var actorSessionId = await SeedSessionAsync(tenantId, actorId);
        var token = await GenerateTokenAsync(host, actorId, tenantId, actorSessionId, ["ADMIN"]);

        var response = await DeleteAsync(client, $"/api/v1/users/{targetUserId}/roles/OPERATOR", token);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task RemoveRole_of_the_tenants_last_active_Administrator_returns_409()
    {
        var tenantId = await SeedTenantAsync();
        var actorId = await SeedUserAsync(tenantId);
        var targetUserId = await SeedUserAsync(tenantId);
        await SeedUserRoleAsync(tenantId, targetUserId, "ADMIN", actorId);
        await SeedUserRoleAsync(tenantId, targetUserId, "OPERATOR", actorId);
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var actorSessionId = await SeedSessionAsync(tenantId, actorId);
        var token = await GenerateTokenAsync(host, actorId, tenantId, actorSessionId, ["ADMIN"]);

        var response = await DeleteAsync(client, $"/api/v1/users/{targetUserId}/roles/ADMIN", token);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task RemoveRole_for_a_nonexistent_role_returns_404()
    {
        var tenantId = await SeedTenantAsync();
        var actorId = await SeedUserAsync(tenantId);
        var targetUserId = await SeedUserAsync(tenantId);
        await SeedUserRoleAsync(tenantId, targetUserId, "OPERATOR", actorId);
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var actorSessionId = await SeedSessionAsync(tenantId, actorId);
        var token = await GenerateTokenAsync(host, actorId, tenantId, actorSessionId, ["ADMIN"]);

        var response = await DeleteAsync(client, $"/api/v1/users/{targetUserId}/roles/NOT_A_REAL_ROLE", token);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ---- Tests: session revocation cascade (real Redis) ------------------------

    [Fact]
    public async Task After_AssignRole_the_targets_old_access_token_is_rejected_with_Redis_available()
    {
        var tenantId = await SeedTenantAsync();
        var actorId = await SeedUserAsync(tenantId);
        var targetUserId = await SeedUserAsync(tenantId);
        await SeedUserRoleAsync(tenantId, targetUserId, "HOUSEKEEPER", actorId);
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var actorSessionId = await SeedSessionAsync(tenantId, actorId);
        var actorToken = await GenerateTokenAsync(host, actorId, tenantId, actorSessionId, ["ADMIN"]);
        var targetSessionId = await SeedSessionAsync(tenantId, targetUserId);
        var targetOldToken = await GenerateTokenAsync(host, targetUserId, tenantId, targetSessionId, ["HOUSEKEEPER"]);

        var assignResponse = await PostAsync(
            client, $"/api/v1/users/{targetUserId}/roles", new AssignRoleRequest("OPERATOR"), actorToken);
        assignResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Any authenticated endpoint works to prove the token itself is now
        // rejected — GetById on the caller's own admin-visible record is the
        // simplest already-available one that requires no extra body.
        var followUp = await GetAsync(client, $"/api/v1/users/{targetUserId}", targetOldToken);

        followUp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
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
}
