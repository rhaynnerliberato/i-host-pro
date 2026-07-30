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
/// End-to-end test of the two password endpoints (Incremento 3, Checkpoint 9)
/// — <c>POST /api/v1/users/me/change-password</c> on <see cref="UsersController"/>
/// and <c>POST /api/v1/users/{userId}/reset-password</c> on
/// <see cref="UserAdministrationController"/> — against the REAL composition
/// root wiring, mirroring <see cref="UserBlockingEndpointsTests"/>'s shape
/// exactly (own Postgres+Redis Fixture, real <c>AddIdentityCommandDispatch</c>,
/// no RabbitMQ). Additionally exercises the real <c>/api/v1/auth/login</c>
/// endpoint to prove both endpoints' effect on authentication itself
/// (Section 9 of the Checkpoint 9 decision).
/// </summary>
public class ChangePasswordEndpointsTests : IClassFixture<ChangePasswordEndpointsTests.Fixture>
{
    private const string OutboxSchema = "identity_messaging";
    private const string KnownPassword = "Correct-Horse-Battery-Staple-42!";
    private const string NewPassword = "New-Correct-Horse-Battery-Staple-43!";
    private const string Issuer = "https://identity.ihostpro.test";
    private const string Audience = "ihostpro-api-test";

    private static readonly JsonSerializerOptions JsonWebDefaults = new(JsonSerializerDefaults.Web);

    private readonly RedisContainer _redisContainer;
    private readonly string _migratorConnectionString;
    private readonly string _appConnectionString;
    private readonly string _signingKeyPem;

    public ChangePasswordEndpointsTests(Fixture fixture)
    {
        _redisContainer = fixture.RedisContainer;
        _migratorConnectionString = fixture.MigratorConnectionString;
        _appConnectionString = fixture.AppConnectionString;

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

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string route, object? body, string? token)
    {
        var request = new HttpRequestMessage(method, route);
        if (body is not null)
            request.Content = JsonContent.Create(body, options: JsonWebDefaults);
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return await client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> LoginAsync(HttpClient client, string tenantSlug, string email, string password) =>
        PostAsync(client, "/api/v1/auth/login", new { tenantSlug, email, password }, token: null);

    // ---- Tests: 401/403 gates ---------------------------------------------

    [Fact]
    public async Task ChangeOwnPassword_without_a_token_returns_401()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();

        var response = await PostAsync(
            client, "/api/v1/users/me/change-password", new { currentPassword = KnownPassword, newPassword = NewPassword }, token: null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ResetPassword_without_a_token_returns_401()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();

        var response = await PostAsync(client, $"/api/v1/users/{Guid.NewGuid()}/reset-password", new { newPassword = NewPassword }, token: null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ResetPassword_with_a_role_lacking_USERS_MANAGE_returns_403()
    {
        var (tenantId, _) = await SeedTenantAsync();
        var (actorId, _) = await SeedUserAsync(tenantId);
        var (targetUserId, _) = await SeedUserAsync(tenantId);
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var sessionId = await SeedSessionAsync(tenantId, actorId);
        var token = await GenerateTokenAsync(host, actorId, tenantId, sessionId, ["HOUSEKEEPER"]);

        var response = await PostAsync(client, $"/api/v1/users/{targetUserId}/reset-password", new { newPassword = NewPassword }, token);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ---- Tests: ChangeOwnPassword -------------------------------------------

    [Fact]
    public async Task A_valid_own_password_change_returns_204_with_no_store_header()
    {
        var (tenantId, _) = await SeedTenantAsync();
        var (userId, _) = await SeedUserAsync(tenantId);
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var sessionId = await SeedSessionAsync(tenantId, userId);
        var token = await GenerateTokenAsync(host, userId, tenantId, sessionId, ["HOUSEKEEPER"]);

        var response = await PostAsync(
            client, "/api/v1/users/me/change-password", new { currentPassword = KnownPassword, newPassword = NewPassword }, token);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
    }

    [Fact]
    public async Task An_incorrect_current_password_returns_400()
    {
        var (tenantId, _) = await SeedTenantAsync();
        var (userId, _) = await SeedUserAsync(tenantId);
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var sessionId = await SeedSessionAsync(tenantId, userId);
        var token = await GenerateTokenAsync(host, userId, tenantId, sessionId, ["HOUSEKEEPER"]);

        var response = await PostAsync(
            client, "/api/v1/users/me/change-password", new { currentPassword = "wrong-password", newPassword = NewPassword }, token);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_new_password_equal_to_the_current_one_returns_400()
    {
        var (tenantId, _) = await SeedTenantAsync();
        var (userId, _) = await SeedUserAsync(tenantId);
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var sessionId = await SeedSessionAsync(tenantId, userId);
        var token = await GenerateTokenAsync(host, userId, tenantId, sessionId, ["HOUSEKEEPER"]);

        var response = await PostAsync(
            client, "/api/v1/users/me/change-password", new { currentPassword = KnownPassword, newPassword = KnownPassword }, token);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task After_a_valid_own_password_change_the_token_used_for_the_request_is_rejected_afterward()
    {
        var (tenantId, slug) = await SeedTenantAsync();
        var (userId, email) = await SeedUserAsync(tenantId);
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var sessionId = await SeedSessionAsync(tenantId, userId);
        var token = await GenerateTokenAsync(host, userId, tenantId, sessionId, ["HOUSEKEEPER"]);

        var changeResponse = await PostAsync(
            client, "/api/v1/users/me/change-password", new { currentPassword = KnownPassword, newPassword = NewPassword }, token);
        changeResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var followUp = await GetAsync(client, "/api/v1/users/me", token);
        followUp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var oldPasswordLogin = await LoginAsync(client, slug, email, KnownPassword);
        oldPasswordLogin.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var newPasswordLogin = await LoginAsync(client, slug, email, NewPassword);
        newPasswordLogin.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ---- Tests: AdminResetPassword -------------------------------------------

    [Fact]
    public async Task A_valid_reset_returns_204_with_no_store_header()
    {
        var (tenantId, _) = await SeedTenantAsync();
        var (actorId, _) = await SeedUserAsync(tenantId);
        var (targetUserId, _) = await SeedUserAsync(tenantId);
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var actorSessionId = await SeedSessionAsync(tenantId, actorId);
        var token = await GenerateTokenAsync(host, actorId, tenantId, actorSessionId, ["ADMIN"]);

        var response = await PostAsync(client, $"/api/v1/users/{targetUserId}/reset-password", new { newPassword = NewPassword }, token);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
    }

    [Fact]
    public async Task Reset_for_a_user_of_a_different_tenant_returns_404()
    {
        var (tenantA, _) = await SeedTenantAsync();
        var (tenantB, _) = await SeedTenantAsync();
        var (actorId, _) = await SeedUserAsync(tenantA);
        var (userInTenantB, _) = await SeedUserAsync(tenantB);
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var actorSessionId = await SeedSessionAsync(tenantA, actorId);
        var token = await GenerateTokenAsync(host, actorId, tenantA, actorSessionId, ["ADMIN"]);

        var response = await PostAsync(client, $"/api/v1/users/{userInTenantB}/reset-password", new { newPassword = NewPassword }, token);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task An_Administrator_resetting_their_own_password_returns_409()
    {
        var (tenantId, _) = await SeedTenantAsync();
        var (actorId, _) = await SeedUserAsync(tenantId);
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var actorSessionId = await SeedSessionAsync(tenantId, actorId);
        var token = await GenerateTokenAsync(host, actorId, tenantId, actorSessionId, ["ADMIN"]);

        var response = await PostAsync(client, $"/api/v1/users/{actorId}/reset-password", new { newPassword = NewPassword }, token);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task After_a_valid_reset_the_targets_old_access_token_is_rejected_but_the_Administrators_own_session_stays_active()
    {
        var (tenantId, slug) = await SeedTenantAsync();
        var (actorId, _) = await SeedUserAsync(tenantId);
        var (targetUserId, targetEmail) = await SeedUserAsync(tenantId);
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var actorSessionId = await SeedSessionAsync(tenantId, actorId);
        var actorToken = await GenerateTokenAsync(host, actorId, tenantId, actorSessionId, ["ADMIN"]);
        var targetSessionId = await SeedSessionAsync(tenantId, targetUserId);
        var targetOldToken = await GenerateTokenAsync(host, targetUserId, tenantId, targetSessionId, ["HOUSEKEEPER"]);

        var resetResponse = await PostAsync(client, $"/api/v1/users/{targetUserId}/reset-password", new { newPassword = NewPassword }, actorToken);
        resetResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var targetFollowUp = await GetAsync(client, $"/api/v1/users/{targetUserId}", targetOldToken);
        targetFollowUp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var actorFollowUp = await GetAsync(client, $"/api/v1/users/{targetUserId}", actorToken);
        actorFollowUp.StatusCode.Should().Be(HttpStatusCode.OK); // the Administrator's own session is untouched

        var oldPasswordLogin = await LoginAsync(client, slug, targetEmail, KnownPassword);
        oldPasswordLogin.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var newPasswordLogin = await LoginAsync(client, slug, targetEmail, NewPassword);
        newPasswordLogin.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Resetting_a_blocked_users_password_succeeds_but_they_still_cannot_log_in_until_unblocked()
    {
        var (tenantId, slug) = await SeedTenantAsync();
        var (actorId, _) = await SeedUserAsync(tenantId);
        var (targetUserId, targetEmail) = await SeedUserAsync(tenantId);
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var actorSessionId = await SeedSessionAsync(tenantId, actorId);
        var actorToken = await GenerateTokenAsync(host, actorId, tenantId, actorSessionId, ["ADMIN"]);
        (await PostAsync(client, $"/api/v1/users/{targetUserId}/block", null, actorToken)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var resetResponse = await PostAsync(client, $"/api/v1/users/{targetUserId}/reset-password", new { newPassword = NewPassword }, actorToken);
        resetResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var loginWithNewPassword = await LoginAsync(client, slug, targetEmail, NewPassword);
        loginWithNewPassword.StatusCode.Should().Be(HttpStatusCode.Unauthorized); // still blocked

        (await PostAsync(client, $"/api/v1/users/{targetUserId}/unblock", null, actorToken)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var loginAfterUnblock = await LoginAsync(client, slug, targetEmail, NewPassword);
        loginAfterUnblock.StatusCode.Should().Be(HttpStatusCode.OK);
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
