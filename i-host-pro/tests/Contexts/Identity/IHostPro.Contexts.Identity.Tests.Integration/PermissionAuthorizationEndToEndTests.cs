using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using FluentAssertions;
using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.BuildingBlocks.Infrastructure.Persistence;
using IHostPro.Contexts.Identity.Api.Authorization;
using IHostPro.Contexts.Identity.Application;
using IHostPro.Contexts.Identity.Contracts.Authorization;
using IHostPro.Contexts.Identity.Infrastructure;
using IHostPro.Contexts.Identity.Infrastructure.Authentication;
using IHostPro.Contexts.Identity.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Testcontainers.PostgreSql;

namespace IHostPro.Contexts.Identity.Tests.Integration;

/// <summary>
/// End-to-end test of permission-based authorization (Incremento 3,
/// Checkpoint 2): a real <c>IHost</c> (<c>AddIdentityModule</c>/
/// <c>AddIdentityJwtIssuance</c>/<c>AddIdentityJwtBearerAuthentication</c>/
/// <c>AddIdentityAuthorization</c> — the exact composition root
/// <c>IHostPro.Api</c>'s <c>Program.cs</c> wires), a real PostgreSQL instance
/// with the real seeded catalog (migrated exactly as production does), and
/// the real <see cref="IJwtTokenGenerator"/> resolved from that same host's
/// DI container — never a hand-built token. Only one test-only minimal
/// endpoint is mapped (never a controller, never in a production project),
/// mirroring the already-established pattern in
/// <c>JwtBearerAuthenticationTests</c> ("/protected") — this checkpoint does
/// not implement any real controller (Incremento 3 plan, Checkpoint 2,
/// Section 6: "não implementar ainda controllers").
///
/// No Redis, no RabbitMQ/Wolverine outbox: this test exercises no session
/// revocation and no Integration Event publication, so the module's default
/// no-op <c>ISessionRevocationCache</c> (already registered by
/// <c>AddIdentityModule</c>) is used as-is, and no outbox is provisioned.
/// </summary>
public class PermissionAuthorizationEndToEndTests : IClassFixture<PermissionAuthorizationEndToEndTests.Fixture>
{
    private const string Issuer = "https://identity.ihostpro.test";
    private const string Audience = "ihostpro-api-test";
    private const string ProtectedRoute = "/test/users-manage";

    private readonly string _appConnectionString;
    private readonly string _signingKeyPem;

    public PermissionAuthorizationEndToEndTests(Fixture fixture)
    {
        _appConnectionString = fixture.AppConnectionString;

        // Deliberately NOT shared via Fixture — see JwtBearerAuthenticationTests'
        // constructor doc comment for the full Windows CNG native-handle-sharing
        // rationale (Etapa 15A stabilization).
        using var signingKey = RSA.Create(2048);
        _signingKeyPem = signingKey.ExportRSAPrivateKeyPem();
    }

    public sealed class Fixture : IAsyncLifetime
    {
        private const string AppRolePassword = "test_app_password";
        private const string MigratorRolePassword = "test_migrator_password";

        private PostgreSqlContainer _postgresContainer = null!;
        public string AppConnectionString { get; private set; } = null!;

        public async Task InitializeAsync()
        {
            _postgresContainer = new PostgreSqlBuilder()
                .WithImage("postgres:16")
                .WithDatabase("ihostpro_test")
                .WithUsername("ihostpro")
                .WithPassword("ihostpro_dev")
                .Build();

            await _postgresContainer.StartAsync();

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
            var migratorConnectionString = builder.ConnectionString;
            builder.Username = "ihostpro_app";
            builder.Password = AppRolePassword;
            AppConnectionString = builder.ConnectionString;

            var options = new DbContextOptionsBuilder<IdentityDbContext>()
                .UseNpgsql(migratorConnectionString, npgsqlOptions => npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "identity"))
                .Options;

            await using var migratorDbContext = new IdentityDbContext(options, new TenantContext());
            await migratorDbContext.Database.MigrateAsync();
        }

        public async Task DisposeAsync() => await _postgresContainer.DisposeAsync();
    }

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
                    services.AddRouting();
                    services.AddScoped<ITenantContext, TenantContext>();
                    services.AddScoped<ICurrentTenantProvider, TenantContextCurrentTenantProvider>();
                    services.AddIHostProTenantAwarePipeline();
                    services.AddIdentityModule(configuration, isDevelopmentEnvironment: false);
                    services.AddIdentityJwtIssuance(configuration);
                    services.AddIdentityJwtBearerAuthentication();
                    services.AddIdentityAuthorization();
                });
                webHost.Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints =>
                    {
                        // Test infrastructure only — no controller, no
                        // production project touched (Incremento 3 plan,
                        // Checkpoint 2, Section 6).
                        endpoints.MapGet(ProtectedRoute, () => "ok")
                            .RequireAuthorization(IdentityPermissionCodes.UsersManage);
                    });
                });
            });

        return await hostBuilder.StartAsync();
    }

    private static async Task<string> GenerateTokenAsync(IHost host, string[] roles)
    {
        using var scope = host.Services.CreateScope();
        var generator = scope.ServiceProvider.GetRequiredService<IJwtTokenGenerator>();

        var request = new JwtAccessTokenRequest(
            UserId: Guid.NewGuid(), TenantId: Guid.NewGuid(), SessionId: Guid.NewGuid(), Roles: roles);

        return generator.GenerateAccessToken(request).Token;
    }

    private static async Task<HttpResponseMessage> CallProtectedRouteAsync(HttpClient client, string? token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, ProtectedRoute);
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return await client.SendAsync(request);
    }

    [Fact]
    public async Task A_token_for_a_role_granting_the_permission_satisfies_the_policy()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var token = await GenerateTokenAsync(host, ["ADMIN"]);

        var response = await CallProtectedRouteAsync(client, token);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_token_for_a_role_not_granting_the_permission_is_denied()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var token = await GenerateTokenAsync(host, ["HOUSEKEEPER"]);

        var response = await CallProtectedRouteAsync(client, token);

        // Proves the policy is not satisfied merely by being authenticated —
        // this token is cryptographically valid and belongs to a real,
        // catalogued role, yet is still rejected.
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_token_with_multiple_roles_is_authorized_when_their_union_grants_the_permission()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var token = await GenerateTokenAsync(host, ["OPERATOR", "ADMIN"]);

        var response = await CallProtectedRouteAsync(client, token);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_token_with_no_roles_is_denied()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var token = await GenerateTokenAsync(host, []);

        var response = await CallProtectedRouteAsync(client, token);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_missing_token_is_rejected()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();

        var response = await CallProtectedRouteAsync(client, token: null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
