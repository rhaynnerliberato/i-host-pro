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
using IHostPro.Contexts.Configuration.Api.Controllers;
using IHostPro.Contexts.Configuration.Infrastructure;
using IHostPro.Contexts.Configuration.Infrastructure.Persistence;
using IHostPro.Contexts.Identity.Api.Authorization;
using IHostPro.Contexts.Identity.Application;
using IHostPro.Contexts.Identity.Infrastructure;
using IHostPro.Contexts.Identity.Infrastructure.Authentication;
using IHostPro.Contexts.Identity.Infrastructure.Persistence;
using JasperFx;
using JasperFx.Resources;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Testcontainers.PostgreSql;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.Postgresql;

namespace IHostPro.Contexts.Configuration.Tests.Integration;

/// <summary>
/// End-to-end test of <see cref="TemplatesController"/> against the real
/// composition root wiring — mirrors <c>ConfigurationEndpointsTests</c>'s
/// own Fixture/host-building structure exactly (Fase 9, Checkpoint 1). Per
/// the seeded catalog (<c>IdentityCatalogSeed</c>): ADMIN has
/// <c>TEMPLATES:MANAGE</c>, AI_AGENT has <c>TEMPLATES:READ</c> — the
/// permissions this checkpoint reuses, never invents.
/// </summary>
public class TemplatesEndpointsTests : IClassFixture<TemplatesEndpointsTests.Fixture>
{
    private const string IdentityOutboxSchema = "identity_messaging";
    private const string ConfigurationOutboxSchema = "configuration_messaging";
    private const string MainSchema = "platform_messaging";
    private const string Issuer = "https://identity.ihostpro.test";
    private const string Audience = "ihostpro-api-test";

    private static readonly JsonSerializerOptions JsonWebDefaults = new(JsonSerializerDefaults.Web);

    private readonly string _appConnectionString;
    private readonly string _signingKeyPem;

    public TemplatesEndpointsTests(Fixture fixture)
    {
        _appConnectionString = fixture.AppConnectionString;

        using var signingKey = RSA.Create(2048);
        _signingKeyPem = signingKey.ExportRSAPrivateKeyPem();
    }

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

            await using (var identityDbContext = CreateIdentityDbContext(MigratorConnectionString))
                await identityDbContext.Database.MigrateAsync();
            await using (var configurationDbContext = CreateConfigurationDbContext(MigratorConnectionString))
                await configurationDbContext.Database.MigrateAsync();

            await ProvisionMainStoreAsMigratorAsync();
            await ProvisionOutboxAsMigratorAsync(IdentityOutboxSchema, typeof(IdentityDbContext));
            await ProvisionOutboxAsMigratorAsync(ConfigurationOutboxSchema, typeof(ConfigurationDbContext));
        }

        public async Task DisposeAsync() => await _container.DisposeAsync();

        private static IdentityDbContext CreateIdentityDbContext(string connectionString)
        {
            var options = new DbContextOptionsBuilder<IdentityDbContext>()
                .UseNpgsql(connectionString, npgsqlOptions => npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "identity"))
                .Options;
            return new IdentityDbContext(options, new TenantContext());
        }

        private static ConfigurationDbContext CreateConfigurationDbContext(string connectionString)
        {
            var options = new DbContextOptionsBuilder<ConfigurationDbContext>()
                .UseNpgsql(connectionString, npgsqlOptions => npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "configuration"))
                .Options;
            return new ConfigurationDbContext(options, new TenantContext());
        }

        private async Task ProvisionMainStoreAsMigratorAsync()
        {
            var hostBuilder = Host.CreateApplicationBuilder();
            hostBuilder.UseWolverine(opts =>
            {
                opts.PersistMessagesWithPostgresql(MigratorConnectionString, MainSchema);
                opts.AutoBuildMessageStorageOnStartup = AutoCreate.None;
                opts.UseEntityFrameworkCoreTransactions();
            });

            using var mainHost = hostBuilder.Build();
            await mainHost.SetupResources();

            await GrantSchemaAsync(MainSchema);
        }

        private async Task ProvisionOutboxAsMigratorAsync(string schema, Type dbContextMarkerType)
        {
            var hostBuilder = Host.CreateApplicationBuilder();
            hostBuilder.UseWolverine(opts =>
            {
                opts.EnrollAncillaryPostgresqlOutbox(MigratorConnectionString, schema, dbContextMarkerType);
                opts.AutoBuildMessageStorageOnStartup = AutoCreate.None;
                opts.UseEntityFrameworkCoreTransactions();
            });

            using var outboxHost = hostBuilder.Build();
            await outboxHost.SetupResources();

            await GrantSchemaAsync(schema);
        }

        private async Task GrantSchemaAsync(string schema)
        {
            await using var connection = new NpgsqlConnection(MigratorConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                GRANT USAGE ON SCHEMA {schema} TO ihostpro_app;
                GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA {schema} TO ihostpro_app;
                GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA {schema} TO ihostpro_app;
                ALTER DEFAULT PRIVILEGES FOR ROLE ihostpro_migrator IN SCHEMA {schema}
                  GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO ihostpro_app;
                ALTER DEFAULT PRIVILEGES FOR ROLE ihostpro_migrator IN SCHEMA {schema}
                  GRANT USAGE, SELECT ON SEQUENCES TO ihostpro_app;
                """;
            await command.ExecuteNonQueryAsync();
        }
    }

    // ---- Server ---------------------------------------------------------

    private async Task<IHost> BuildHostAsync()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Identity"] = _appConnectionString,
            ["ConnectionStrings:Configuration"] = _appConnectionString,
            // No Template test in this suite exercises the policy cache —
            // a syntactically valid but unreachable address is deliberate,
            // same as ConfigurationEndpointsTests' own precedent.
            ["Configuration:PolicyCache:ConnectionString"] = "localhost:1",
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
                    services.AddControllers().AddApplicationPart(typeof(TemplatesController).Assembly);
                    services.AddScoped<ITenantContext, TenantContext>();
                    services.AddScoped<ICurrentTenantProvider, TenantContextCurrentTenantProvider>();
                    services.AddIHostProTenantAwarePipeline();

                    services.AddIdentityModule(configuration, isDevelopmentEnvironment: false);
                    services.AddIdentityJwtIssuance(configuration);
                    services.AddIdentityJwtBearerAuthentication();
                    services.AddIdentityAuthorization();

                    services.AddConfigurationModule(configuration);
                    services.AddConfigurationCommandDispatch();
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
                opts.PersistMessagesWithPostgresql(_appConnectionString, MainSchema);
                opts.EnrollAncillaryPostgresqlOutbox(_appConnectionString, ConfigurationOutboxSchema, typeof(ConfigurationDbContext));
                opts.AutoBuildMessageStorageOnStartup = AutoCreate.None;
                opts.UseEntityFrameworkCoreTransactions();
            });

        return await hostBuilder.StartAsync();
    }

    private static async Task<string> GenerateTokenAsync(IHost host, Guid userId, Guid tenantId, string[] roles)
    {
        using var scope = host.Services.CreateScope();
        var generator = scope.ServiceProvider.GetRequiredService<IJwtTokenGenerator>();
        var request = new JwtAccessTokenRequest(UserId: userId, TenantId: tenantId, SessionId: Guid.NewGuid(), Roles: roles);
        return generator.GenerateAccessToken(request).Token;
    }

    // ---- HTTP helpers -----------------------------------------------------

    private static Task<HttpResponseMessage> GetAsync(HttpClient client, string route, string? token) =>
        SendAsync(client, HttpMethod.Get, route, null, token);

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string route, object? body, string? token) =>
        SendAsync(client, HttpMethod.Post, route, body, token);

    private static Task<HttpResponseMessage> PutAsync(HttpClient client, string route, object? body, string? token) =>
        SendAsync(client, HttpMethod.Put, route, body, token);

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string route, object? body, string? token)
    {
        var request = new HttpRequestMessage(method, route);
        if (body is not null)
            request.Content = JsonContent.Create(body, options: JsonWebDefaults);
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return await client.SendAsync(request);
    }

    private static async Task<string?> ReadProblemTitleAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.TryGetProperty("title", out var title) ? title.GetString() : null;
    }

    // ---- Authentication/authorization -------------------------------------

    [Fact]
    public async Task Create_without_a_token_returns_401()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();

        var response = await PostAsync(client, "/api/v1/templates", new { key = "K", content = "c" }, token: null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Create_with_a_role_lacking_TEMPLATES_MANAGE_returns_403()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), Guid.NewGuid(), ["AI_AGENT"]);

        var response = await PostAsync(client, "/api/v1/templates", new { key = "K", content = "c" }, token);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GetByKey_with_a_role_lacking_TEMPLATES_READ_returns_403()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), Guid.NewGuid(), ["HOUSEKEEPER"]);

        var response = await GetAsync(client, "/api/v1/templates/K", token);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ---- Create / GetByKey round trip ----

    [Fact]
    public async Task Create_as_ADMIN_then_GetByKey_as_AI_AGENT_returns_the_created_template()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var tenantId = Guid.NewGuid();
        var manageToken = await GenerateTokenAsync(host, Guid.NewGuid(), tenantId, ["ADMIN"]);
        var readToken = await GenerateTokenAsync(host, Guid.NewGuid(), tenantId, ["AI_AGENT"]);

        var createResponse = await PostAsync(client, "/api/v1/templates", new { key = "RESERVATION_CONFIRMATION", content = "Olá {{GuestName}}" }, manageToken);
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        var getResponse = await GetAsync(client, "/api/v1/templates/RESERVATION_CONFIRMATION", readToken);
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await getResponse.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("key").GetString().Should().Be("RESERVATION_CONFIRMATION");
        body.GetProperty("content").GetString().Should().Be("Olá {{GuestName}}");
        body.GetProperty("isActive").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Create_with_a_duplicate_key_for_the_same_tenant_returns_409_template_key_already_exists()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), Guid.NewGuid(), ["ADMIN"]);
        await PostAsync(client, "/api/v1/templates", new { key = "K", content = "first" }, token);

        var response = await PostAsync(client, "/api/v1/templates", new { key = "K", content = "second" }, token);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ReadProblemTitleAsync(response)).Should().Be("template_key_already_exists");
    }

    [Fact]
    public async Task GetByKey_for_an_unknown_key_returns_404_template_not_found()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), Guid.NewGuid(), ["AI_AGENT"]);

        var response = await GetAsync(client, "/api/v1/templates/NOT_A_REAL_KEY", token);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ReadProblemTitleAsync(response)).Should().Be("template_not_found");
    }

    // ---- UpdateContent ----

    [Fact]
    public async Task UpdateContent_replaces_the_content_of_an_existing_template()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var tenantId = Guid.NewGuid();
        var manageToken = await GenerateTokenAsync(host, Guid.NewGuid(), tenantId, ["ADMIN"]);
        var readToken = await GenerateTokenAsync(host, Guid.NewGuid(), tenantId, ["AI_AGENT"]);
        await PostAsync(client, "/api/v1/templates", new { key = "K", content = "original" }, manageToken);

        var updateResponse = await PutAsync(client, "/api/v1/templates/K", new { content = "updated" }, manageToken);
        updateResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var getResponse = await GetAsync(client, "/api/v1/templates/K", readToken);
        var body = await getResponse.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("content").GetString().Should().Be("updated");
    }

    [Fact]
    public async Task UpdateContent_for_an_unknown_key_returns_404_template_not_found()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), Guid.NewGuid(), ["ADMIN"]);

        var response = await PutAsync(client, "/api/v1/templates/MISSING", new { content = "updated" }, token);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ReadProblemTitleAsync(response)).Should().Be("template_not_found");
    }

    // ---- Activate / Deactivate ----

    [Fact]
    public async Task Deactivate_then_Activate_round_trips_isActive()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var tenantId = Guid.NewGuid();
        var manageToken = await GenerateTokenAsync(host, Guid.NewGuid(), tenantId, ["ADMIN"]);
        var readToken = await GenerateTokenAsync(host, Guid.NewGuid(), tenantId, ["AI_AGENT"]);
        await PostAsync(client, "/api/v1/templates", new { key = "K", content = "content" }, manageToken);

        var deactivateResponse = await PostAsync(client, "/api/v1/templates/K/deactivate", null, manageToken);
        deactivateResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        (await deactivateResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("isActive").GetBoolean().Should().BeFalse();

        var getAfterDeactivate = await GetAsync(client, "/api/v1/templates/K", readToken);
        (await getAfterDeactivate.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("isActive").GetBoolean().Should().BeFalse();

        var activateResponse = await PostAsync(client, "/api/v1/templates/K/activate", null, manageToken);
        activateResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        (await activateResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("isActive").GetBoolean().Should().BeTrue();
    }

    // ---- Tenant isolation ----

    [Fact]
    public async Task A_template_created_by_one_tenant_is_invisible_to_another_tenant()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var tenantAToken = await GenerateTokenAsync(host, Guid.NewGuid(), Guid.NewGuid(), ["ADMIN"]);
        var tenantBReadToken = await GenerateTokenAsync(host, Guid.NewGuid(), Guid.NewGuid(), ["AI_AGENT"]);

        var createResponse = await PostAsync(client, "/api/v1/templates", new { key = "TENANT_A_ONLY", content = "content" }, tenantAToken);
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        var crossTenantResponse = await GetAsync(client, "/api/v1/templates/TENANT_A_ONLY", tenantBReadToken);

        crossTenantResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ReadProblemTitleAsync(crossTenantResponse)).Should().Be("template_not_found");
    }

    [Fact]
    public async Task Two_tenants_may_each_use_the_same_key_independently()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var tenantAToken = await GenerateTokenAsync(host, Guid.NewGuid(), Guid.NewGuid(), ["ADMIN"]);
        var tenantBToken = await GenerateTokenAsync(host, Guid.NewGuid(), Guid.NewGuid(), ["ADMIN"]);

        var tenantAResponse = await PostAsync(client, "/api/v1/templates", new { key = "SHARED_KEY", content = "tenant A content" }, tenantAToken);
        var tenantBResponse = await PostAsync(client, "/api/v1/templates", new { key = "SHARED_KEY", content = "tenant B content" }, tenantBToken);

        tenantAResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        tenantBResponse.StatusCode.Should().Be(HttpStatusCode.Created,
            "the unique index is (tenant_id, key) — the same key must be independently usable per tenant");
    }
}
