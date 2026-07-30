using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.BuildingBlocks.Infrastructure.Persistence;
using IHostPro.Contexts.Identity.Api.Authorization;
using IHostPro.Contexts.Identity.Api.Contracts;
using IHostPro.Contexts.Identity.Api.Controllers;
using IHostPro.Contexts.Identity.Application;
using IHostPro.Contexts.Identity.Infrastructure;
using IHostPro.Contexts.Identity.Infrastructure.Authentication;
using IHostPro.Contexts.Identity.Infrastructure.Persistence;
using IHostPro.Contexts.Identity.Infrastructure.Seed;
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
/// End-to-end test of the two read-only catalog HTTP endpoints (Incremento 3,
/// Checkpoint 3), against a real <c>IHost</c> composed exactly the way
/// <c>IHostPro.Api</c>'s <c>Program.cs</c> wires it for these concerns
/// (<c>AddIdentityModule</c>/<c>AddIdentityJwtIssuance</c>/
/// <c>AddIdentityJwtBearerAuthentication</c>/<c>AddIdentityAuthorization</c>/
/// <c>AddIdentityCommandDispatch</c>), a real PostgreSQL instance with the
/// real seeded catalog, the real <see cref="IJwtTokenGenerator"/>, and — unlike
/// <see cref="PermissionAuthorizationEndToEndTests"/> (Checkpoint 2, which
/// mapped a test-only minimal endpoint because no controller existed yet) —
/// the REAL <see cref="RolesController"/>/<see cref="PermissionsController"/>,
/// discovered via <c>AddApplicationPart</c> exactly like
/// <see cref="AuthEndpointsTests"/> does for <c>AuthController</c>.
///
/// No Redis, no RabbitMQ/Wolverine outbox: <c>ListRolesQuery</c>/
/// <c>ListPermissionsQuery</c> go through the plain, shared, generic
/// <c>TenantTransactionBehavior&lt;,&gt;</c> (backed only by
/// <c>ITenantAwareUnitOfWork</c>, itself backed only by the already-registered
/// <c>DbContext</c> alias — see <c>IdentityCommandDispatchExtensions</c>'s doc
/// comment), never <c>IIdentityTransactionExecutor</c>, so none of
/// <c>AuthEndpointsTests</c>' outbox provisioning is needed here. Session
/// revocation still runs on every authenticated request
/// (<c>ConfigureJwtBearerOptions.OnTokenValidatedAsync</c>), but
/// <c>AddIdentityModule</c>'s default no-op <see cref="ISessionRevocationCache"/>
/// (never overridden by <c>AddIdentitySessionRevocationCache</c> here) is
/// sufficient — the exact same choice <c>PermissionAuthorizationEndToEndTests</c>
/// already made.
/// </summary>
public class CatalogEndpointsTests : IClassFixture<CatalogEndpointsTests.Fixture>
{
    private const string Issuer = "https://identity.ihostpro.test";
    private const string Audience = "ihostpro-api-test";
    private const string RolesRoute = "/api/v1/roles";
    private const string PermissionsRoute = "/api/v1/permissions";

    private static readonly JsonSerializerOptions JsonWebDefaults = new(JsonSerializerDefaults.Web);

    private readonly string _appConnectionString;
    private readonly string _signingKeyPem;

    public CatalogEndpointsTests(Fixture fixture)
    {
        _appConnectionString = fixture.AppConnectionString;

        // Deliberately NOT shared via Fixture — see JwtBearerAuthenticationTests'
        // constructor doc comment for the full Windows CNG native-handle-sharing
        // rationale (Etapa 15A stabilization).
        using var signingKey = RSA.Create(2048);
        _signingKeyPem = signingKey.ExportRSAPrivateKeyPem();
    }

    /// <summary>
    /// Started once per test class, not once per test method — same rationale
    /// as <c>IdentityRowLevelSecurityTests.Fixture</c> (Docker daemon load).
    /// </summary>
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
                    services.AddControllers().AddApplicationPart(typeof(RolesController).Assembly);
                    services.AddScoped<ITenantContext, TenantContext>();
                    services.AddScoped<ICurrentTenantProvider, TenantContextCurrentTenantProvider>();
                    services.AddIHostProTenantAwarePipeline();
                    services.AddIdentityModule(configuration, isDevelopmentEnvironment: false);
                    services.AddIdentityJwtIssuance(configuration);
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
            });

        return await hostBuilder.StartAsync();
    }

    private static async Task<string> GenerateTokenAsync(IHost host, Guid userId, Guid tenantId, Guid sessionId, string[] roles)
    {
        using var scope = host.Services.CreateScope();
        var generator = scope.ServiceProvider.GetRequiredService<IJwtTokenGenerator>();

        var request = new JwtAccessTokenRequest(UserId: userId, TenantId: tenantId, SessionId: sessionId, Roles: roles);

        return generator.GenerateAccessToken(request).Token;
    }

    private static async Task<HttpResponseMessage> GetAsync(HttpClient client, string route, string? token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, route);
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return await client.SendAsync(request);
    }

    // ---- No token -> 401 ----------------------------------------------------

    [Fact]
    public async Task Roles_without_a_token_returns_401()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();

        var response = await GetAsync(client, RolesRoute, token: null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Permissions_without_a_token_returns_401()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();

        var response = await GetAsync(client, PermissionsRoute, token: null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ---- Authenticated but lacking the specific permission -> 403 -----------

    [Fact]
    public async Task Roles_for_a_role_without_ROLES_READ_returns_403()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), ["HOUSEKEEPER"]);

        var response = await GetAsync(client, RolesRoute, token);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Permissions_for_a_role_without_PERMISSIONS_READ_returns_403()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), ["HOUSEKEEPER"]);

        var response = await GetAsync(client, PermissionsRoute, token);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_role_with_substantial_other_permissions_but_not_ROLES_READ_is_still_denied()
    {
        // OPERATOR is a real, catalogued role granted several permissions
        // (RESERVATIONS:MANAGE, SCHEDULE:MANAGE, CLEANINGS:MANAGE, AUDIT:READ,
        // DASHBOARD:READ, REPORTS:READ...) — proving being authenticated AND
        // broadly authorized elsewhere still does not satisfy this specific
        // policy, unlike ADMIN.
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), ["OPERATOR"]);

        var response = await GetAsync(client, RolesRoute, token);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ---- ADMIN -> 200 with content matching PostgreSQL -----------------------

    [Fact]
    public async Task Roles_for_ADMIN_returns_200_with_the_persisted_catalog()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), ["ADMIN"]);

        var response = await GetAsync(client, RolesRoute, token);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<List<RoleResponse>>(JsonWebDefaults);
        body!.Select(r => r.Code).Should().BeEquivalentTo(IdentityCatalogSeed.Roles.Select(r => r.Id));
        var admin = body.Should().ContainSingle(r => r.Code == "ADMIN").Subject;
        admin.Name.Should().Be("Administrador");
        admin.PermissionCodes.Should().Contain("USERS:MANAGE");
    }

    [Fact]
    public async Task Permissions_for_ADMIN_returns_200_with_the_persisted_catalog()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), ["ADMIN"]);

        var response = await GetAsync(client, PermissionsRoute, token);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<List<PermissionResponse>>(JsonWebDefaults);
        body!.Select(p => p.Code).Should().BeEquivalentTo(IdentityCatalogSeed.Permissions.Select(p => p.Id));
        var usersManage = body.Should().ContainSingle(p => p.Code == "USERS:MANAGE").Subject;
        usersManage.Resource.Should().Be("USERS");
        usersManage.Action.Should().Be("MANAGE");
    }

    // ---- Global catalog: identical across tenants -----------------------------

    [Fact]
    public async Task Two_different_authorized_tenants_receive_the_identical_global_catalog()
    {
        using var host = await BuildHostAsync();
        using var clientA = host.GetTestClient();
        using var clientB = host.GetTestClient();
        var tokenForTenantA = await GenerateTokenAsync(host, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), ["ADMIN"]);
        var tokenForTenantB = await GenerateTokenAsync(host, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), ["ADMIN"]);

        var responseA = await GetAsync(clientA, RolesRoute, tokenForTenantA);
        var responseB = await GetAsync(clientB, RolesRoute, tokenForTenantB);

        responseA.StatusCode.Should().Be(HttpStatusCode.OK);
        responseB.StatusCode.Should().Be(HttpStatusCode.OK);
        var bodyA = await responseA.Content.ReadAsStringAsync();
        var bodyB = await responseB.Content.ReadAsStringAsync();
        bodyA.Should().Be(bodyB, "roles/permissions are a platform catalog, never tenant-scoped data");
    }

    // ---- No user/session/token/tenant information leaks into the payload -------

    [Fact]
    public async Task The_payload_contains_no_user_session_or_tenant_information()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var userId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var token = await GenerateTokenAsync(host, userId, tenantId, sessionId, ["ADMIN"]);

        var rolesResponse = await GetAsync(client, RolesRoute, token);
        var permissionsResponse = await GetAsync(client, PermissionsRoute, token);

        var rolesBody = await rolesResponse.Content.ReadAsStringAsync();
        var permissionsBody = await permissionsResponse.Content.ReadAsStringAsync();
        foreach (var body in new[] { rolesBody, permissionsBody })
        {
            body.Should().NotContain(userId.ToString());
            body.Should().NotContain(tenantId.ToString());
            body.Should().NotContain(sessionId.ToString());
            body.Should().NotContain(token);
        }
    }
}
