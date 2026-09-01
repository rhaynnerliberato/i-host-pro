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
using IHostPro.Contexts.Configuration.Domain;
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
/// End-to-end test of <see cref="PoliciesController"/> against the real
/// composition root wiring: real ASP.NET Core host/TestServer, real JWT
/// (issued by Identity's own real stack), real PostgreSQL for Identity
/// (permission catalog) and Configuration — mirrors
/// <c>ReservationsEndpointsTests</c>'s structure, minus the Property
/// Management dependency Reservations needs and this context does not. Per
/// the seeded catalog (<c>IdentityCatalogSeed</c>): only ADMIN has
/// <c>POLICIES:MANAGE</c>; only AI_AGENT has <c>POLICIES:READ</c> — the two
/// policies are deliberately asymmetric, not hierarchical, so tests use
/// whichever role each endpoint actually requires.
/// </summary>
public class ConfigurationEndpointsTests : IClassFixture<ConfigurationEndpointsTests.Fixture>
{
    private const string IdentityOutboxSchema = "identity_messaging";
    private const string ConfigurationOutboxSchema = "configuration_messaging";
    private const string MainSchema = "platform_messaging";
    private const string Issuer = "https://identity.ihostpro.test";
    private const string Audience = "ihostpro-api-test";

    private static readonly JsonSerializerOptions JsonWebDefaults = new(JsonSerializerDefaults.Web);

    private readonly string _migratorConnectionString;
    private readonly string _appConnectionString;
    private readonly string _signingKeyPem;

    public ConfigurationEndpointsTests(Fixture fixture)
    {
        _migratorConnectionString = fixture.MigratorConnectionString;
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
            // Checkpoint 6: AddConfigurationModule now also wires the policy
            // cache — this suite is about the administrative HTTP surface,
            // not caching (RedisPolicyValueCacheTests owns that), so a
            // syntactically valid but unreachable address is deliberate; the
            // cache degrades to PostgreSQL exactly as it would in a real
            // outage.
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
                    services.AddControllers().AddApplicationPart(typeof(PoliciesController).Assembly);
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

    // ---- Seeding ----------------------------------------------------------

    private async Task SeedTenantValueAsync(Guid tenantId, string policyCode, string reason, string rawValue)
    {
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);

        var options = new DbContextOptionsBuilder<ConfigurationDbContext>()
            .UseNpgsql(_migratorConnectionString, npgsqlOptions => npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "configuration"))
            .Options;
        await using var dbContext = new ConfigurationDbContext(options, tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await dbContext.Database.ExecuteSqlInterpolatedAsync($"SELECT set_config('app.tenant_id', {tenantId.ToString()}, true)");

        var value = PolicyValue.CreateInitialVersion(
            Guid.NewGuid(), tenantId, policyCode, PolicyScope.Tenant(), rawValue, DateTimeOffset.UtcNow, Guid.NewGuid(), reason);
        dbContext.PolicyValues.Add(value);

        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();
    }

    // ---- HTTP helpers ---------------------------------------------------

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

    private static async Task<string?> ReadProblemTitleAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.TryGetProperty("title", out var title) ? title.GetString() : null;
    }

    // ---- Authentication/authorization ------------------------------------

    [Fact]
    public async Task List_without_a_token_returns_401()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();

        var response = await GetAsync(client, "/api/v1/policies", token: null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task List_with_a_role_lacking_POLICIES_READ_returns_403()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), Guid.NewGuid(), ["HOUSEKEEPER"]);

        var response = await GetAsync(client, "/api/v1/policies", token);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task CreateVersion_with_a_role_lacking_POLICIES_MANAGE_returns_403()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), Guid.NewGuid(), ["AI_AGENT"]);

        var response = await PostAsync(
            client, "/api/v1/policies/EARLY_CHECKIN/values",
            new { scopeType = "Tenant", value = new { allowed = true, requiresCleaningCompleted = false, requiresForm = false, notifyFrontDesk = false }, reason = "test" },
            token);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ---- List ----

    [Fact]
    public async Task List_as_AI_AGENT_returns_the_three_seeded_definitions()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), Guid.NewGuid(), ["AI_AGENT"]);

        var response = await GetAsync(client, "/api/v1/policies", token);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.EnumerateArray().Select(e => e.GetProperty("code").GetString())
            .Should().BeEquivalentTo(["EARLY_CHECKIN", "LATE_CHECKOUT", "AI_AGENT_BEHAVIOR"]);
    }

    // ---- GetValue ----

    [Fact]
    public async Task GetValue_for_an_unknown_policy_code_returns_404_policy_not_found()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), Guid.NewGuid(), ["AI_AGENT"]);

        var response = await GetAsync(client, "/api/v1/policies/NOT_A_REAL_CODE/values?scopeType=Tenant", token);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ReadProblemTitleAsync(response)).Should().Be("policy_not_found");
    }

    [Fact]
    public async Task GetValue_at_Global_scope_returns_400_forbidden()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), Guid.NewGuid(), ["AI_AGENT"]);

        var response = await GetAsync(client, "/api/v1/policies/EARLY_CHECKIN/values?scopeType=Global", token);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ReadProblemTitleAsync(response)).Should().Be("forbidden");
    }

    [Fact]
    public async Task GetValue_with_no_value_at_the_scope_returns_404_policy_not_configured()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), Guid.NewGuid(), ["AI_AGENT"]);

        var response = await GetAsync(client, "/api/v1/policies/EARLY_CHECKIN/values?scopeType=Tenant", token);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ReadProblemTitleAsync(response)).Should().Be("policy_not_configured");
    }

    [Fact]
    public async Task GetValue_returns_the_seeded_current_value()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var tenantId = Guid.NewGuid();
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), tenantId, ["AI_AGENT"]);
        await SeedTenantValueAsync(tenantId, "EARLY_CHECKIN", "initial setup", """{"allowed":true,"requiresCleaningCompleted":false,"requiresForm":false,"notifyFrontDesk":false}""");

        var response = await GetAsync(client, "/api/v1/policies/EARLY_CHECKIN/values?scopeType=Tenant", token);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("version").GetInt32().Should().Be(1);
        body.GetProperty("value").GetProperty("allowed").GetBoolean().Should().BeTrue();
    }

    // ---- GetEffective ----

    [Fact]
    public async Task GetEffective_for_an_unknown_policy_code_returns_404_policy_not_found()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), Guid.NewGuid(), ["AI_AGENT"]);

        var response = await GetAsync(client, "/api/v1/policies/NOT_A_REAL_CODE/effective", token);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ReadProblemTitleAsync(response)).Should().Be("policy_not_found");
    }

    [Fact]
    public async Task GetEffective_with_nothing_configured_returns_200_with_NotConfigured_status()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), Guid.NewGuid(), ["AI_AGENT"]);

        var response = await GetAsync(client, "/api/v1/policies/LATE_CHECKOUT/effective", token);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("status").GetString().Should().Be("NotConfigured");
        body.GetProperty("value").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task GetEffective_resolves_the_seeded_tenant_value()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var tenantId = Guid.NewGuid();
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), tenantId, ["AI_AGENT"]);
        await SeedTenantValueAsync(tenantId, "EARLY_CHECKIN", "initial setup", """{"allowed":true,"requiresCleaningCompleted":false,"requiresForm":false,"notifyFrontDesk":false}""");

        var response = await GetAsync(client, "/api/v1/policies/EARLY_CHECKIN/effective", token);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("status").GetString().Should().Be("Resolved");
        body.GetProperty("resolvedScope").GetString().Should().Be("Tenant");
        body.GetProperty("version").GetInt32().Should().Be(1);
    }

    // ---- CreateVersion ----

    [Fact]
    public async Task CreateVersion_as_ADMIN_succeeds_and_returns_201()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), Guid.NewGuid(), ["ADMIN"]);

        var response = await PostAsync(
            client, "/api/v1/policies/EARLY_CHECKIN/values",
            new { scopeType = "Tenant", value = new { allowed = true, requiresCleaningCompleted = true, requiresForm = false, notifyFrontDesk = true }, reason = "initial setup" },
            token);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("version").GetInt32().Should().Be(1);
        body.GetProperty("isCurrent").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task CreateVersion_for_an_unknown_policy_code_returns_404_policy_not_found()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), Guid.NewGuid(), ["ADMIN"]);

        var response = await PostAsync(
            client, "/api/v1/policies/NOT_A_REAL_CODE/values",
            new { scopeType = "Tenant", value = new { allowed = true }, reason = "test" },
            token);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ReadProblemTitleAsync(response)).Should().Be("policy_not_found");
    }

    [Fact]
    public async Task CreateVersion_at_Global_scope_returns_403_forbidden()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), Guid.NewGuid(), ["ADMIN"]);

        var response = await PostAsync(
            client, "/api/v1/policies/EARLY_CHECKIN/values",
            new { scopeType = "Global", value = new { allowed = true }, reason = "test" },
            token);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ReadProblemTitleAsync(response)).Should().Be("forbidden");
    }

    [Fact]
    public async Task CreateVersion_for_Property_scope_without_a_propertyId_returns_400_scope_not_supported()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), Guid.NewGuid(), ["ADMIN"]);

        var response = await PostAsync(
            client, "/api/v1/policies/EARLY_CHECKIN/values",
            new { scopeType = "Property", value = new { allowed = true }, reason = "test" },
            token);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadProblemTitleAsync(response)).Should().Be("scope_not_supported");
    }

    [Fact]
    public async Task CreateVersion_with_a_malformed_value_returns_400_invalid_policy_value()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), Guid.NewGuid(), ["ADMIN"]);

        var response = await PostAsync(
            client, "/api/v1/policies/EARLY_CHECKIN/values",
            new { scopeType = "Tenant", value = new { allowed = "not-a-boolean" }, reason = "test" },
            token);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadProblemTitleAsync(response)).Should().Be("invalid_policy_value");
    }

    [Fact]
    public async Task CreateVersion_for_LATE_CHECKOUT_with_a_percentage_charge_missing_chargeValue_returns_400_invalid_policy_value()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), Guid.NewGuid(), ["ADMIN"]);

        var response = await PostAsync(
            client, "/api/v1/policies/LATE_CHECKOUT/values",
            new
            {
                scopeType = "Tenant",
                value = new { allowed = true, chargeType = "percentage", requiresPix = false, blocksCalendar = false, updatesCleaning = false },
                reason = "test",
            },
            token);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadProblemTitleAsync(response)).Should().Be("invalid_policy_value");
    }

    [Fact]
    public async Task CreateVersion_with_an_empty_reason_returns_400_validation_error()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), Guid.NewGuid(), ["ADMIN"]);

        var response = await PostAsync(
            client, "/api/v1/policies/EARLY_CHECKIN/values",
            new { scopeType = "Tenant", value = new { allowed = true, requiresCleaningCompleted = false, requiresForm = false, notifyFrontDesk = false }, reason = "" },
            token);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadProblemTitleAsync(response)).Should().Be("validation_error");
    }

    [Fact]
    public async Task CreateVersion_with_a_stale_expectedVersion_returns_409_version_conflict()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var tenantId = Guid.NewGuid();
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), tenantId, ["ADMIN"]);
        await SeedTenantValueAsync(tenantId, "EARLY_CHECKIN", "initial setup", """{"allowed":true,"requiresCleaningCompleted":false,"requiresForm":false,"notifyFrontDesk":false}""");

        var response = await PostAsync(
            client, "/api/v1/policies/EARLY_CHECKIN/values",
            new { scopeType = "Tenant", value = new { allowed = false, requiresCleaningCompleted = false, requiresForm = false, notifyFrontDesk = false }, reason = "change", expectedVersion = 99 },
            token);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ReadProblemTitleAsync(response)).Should().Be("version_conflict");
    }

    [Fact]
    public async Task CreateVersion_without_an_expectedVersion_when_one_already_exists_returns_409_version_conflict()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var tenantId = Guid.NewGuid();
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), tenantId, ["ADMIN"]);
        await SeedTenantValueAsync(tenantId, "EARLY_CHECKIN", "initial setup", """{"allowed":true,"requiresCleaningCompleted":false,"requiresForm":false,"notifyFrontDesk":false}""");

        var response = await PostAsync(
            client, "/api/v1/policies/EARLY_CHECKIN/values",
            new { scopeType = "Tenant", value = new { allowed = false, requiresCleaningCompleted = false, requiresForm = false, notifyFrontDesk = false }, reason = "change" },
            token);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ReadProblemTitleAsync(response)).Should().Be("version_conflict");
    }

    [Fact]
    public async Task CreateVersion_with_the_correct_expectedVersion_supersedes_the_previous_row()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var tenantId = Guid.NewGuid();
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), tenantId, ["ADMIN"]);
        await SeedTenantValueAsync(tenantId, "EARLY_CHECKIN", "initial setup", """{"allowed":true,"requiresCleaningCompleted":false,"requiresForm":false,"notifyFrontDesk":false}""");

        var response = await PostAsync(
            client, "/api/v1/policies/EARLY_CHECKIN/values",
            new { scopeType = "Tenant", value = new { allowed = false, requiresCleaningCompleted = false, requiresForm = false, notifyFrontDesk = false }, reason = "change", expectedVersion = 1 },
            token);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("version").GetInt32().Should().Be(2);

        // ADMIN (used above) has POLICIES:MANAGE but not POLICIES:READ per
        // the seeded catalog's deliberate asymmetry — history requires
        // POLICIES:READ, so this reads back with an AI_AGENT token instead.
        var readToken = await GenerateTokenAsync(host, Guid.NewGuid(), tenantId, ["AI_AGENT"]);
        var historyResponse = await GetAsync(client, "/api/v1/policies/EARLY_CHECKIN/history?scopeType=Tenant", readToken);
        historyResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var history = await historyResponse.Content.ReadFromJsonAsync<JsonElement>();
        history.GetArrayLength().Should().Be(2);
        history[0].GetProperty("version").GetInt32().Should().Be(2);
        history[0].GetProperty("isCurrent").GetBoolean().Should().BeTrue();
        history[1].GetProperty("version").GetInt32().Should().Be(1);
        history[1].GetProperty("isCurrent").GetBoolean().Should().BeFalse();
    }

    // ---- History ----

    [Fact]
    public async Task GetHistory_for_an_unknown_policy_code_returns_404_policy_not_found()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), Guid.NewGuid(), ["AI_AGENT"]);

        var response = await GetAsync(client, "/api/v1/policies/NOT_A_REAL_CODE/history?scopeType=Tenant", token);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ReadProblemTitleAsync(response)).Should().Be("policy_not_found");
    }

    [Fact]
    public async Task GetHistory_with_no_values_returns_200_with_an_empty_array()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), Guid.NewGuid(), ["AI_AGENT"]);

        var response = await GetAsync(client, "/api/v1/policies/EARLY_CHECKIN/history?scopeType=Tenant", token);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetArrayLength().Should().Be(0);
    }
}
