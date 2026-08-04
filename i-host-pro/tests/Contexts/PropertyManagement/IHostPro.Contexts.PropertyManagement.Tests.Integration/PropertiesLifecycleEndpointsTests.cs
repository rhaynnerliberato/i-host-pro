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
using IHostPro.Contexts.Identity.Application;
using IHostPro.Contexts.Identity.Infrastructure;
using IHostPro.Contexts.Identity.Infrastructure.Authentication;
using IHostPro.Contexts.Identity.Infrastructure.Persistence;
using IHostPro.Contexts.PropertyManagement.Api.Contracts;
using IHostPro.Contexts.PropertyManagement.Api.Controllers;
using IHostPro.Contexts.PropertyManagement.Application;
using IHostPro.Contexts.PropertyManagement.Domain;
using IHostPro.Contexts.PropertyManagement.Infrastructure;
using IHostPro.Contexts.PropertyManagement.Infrastructure.Persistence;
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

namespace IHostPro.Contexts.PropertyManagement.Tests.Integration;

/// <summary>
/// End-to-end test of <see cref="PropertiesController"/>'s three lifecycle
/// actions against the REAL composition root wiring — mirrors
/// <c>PropertiesEndpointsTests</c> exactly: real ASP.NET Core host/TestServer,
/// real JWT, real PostgreSQL. No RabbitMQ (Checkpoint 4 plan, item 18).
/// </summary>
public class PropertiesLifecycleEndpointsTests : IClassFixture<PropertiesLifecycleEndpointsTests.Fixture>
{
    private const string IdentityOutboxSchema = "identity_messaging";
    private const string PropertyManagementOutboxSchema = "property_management_messaging";
    private const string Issuer = "https://identity.ihostpro.test";
    private const string Audience = "ihostpro-api-test";

    private static readonly JsonSerializerOptions JsonWebDefaults = new(JsonSerializerDefaults.Web);

    private readonly string _migratorConnectionString;
    private readonly string _appConnectionString;
    private readonly string _signingKeyPem;

    public PropertiesLifecycleEndpointsTests(Fixture fixture)
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
            {
                await identityDbContext.Database.MigrateAsync();
            }
            await using (var propertyManagementDbContext = CreatePropertyManagementDbContext(MigratorConnectionString))
            {
                await propertyManagementDbContext.Database.MigrateAsync();
            }

            await ProvisionOutboxAsMigratorAsync(IdentityOutboxSchema, typeof(IdentityDbContext));
            await ProvisionOutboxAsMigratorAsync(PropertyManagementOutboxSchema, typeof(PropertyManagementDbContext));
        }

        public async Task DisposeAsync() => await _container.DisposeAsync();

        private static IdentityDbContext CreateIdentityDbContext(string connectionString)
        {
            var options = new DbContextOptionsBuilder<IdentityDbContext>()
                .UseNpgsql(connectionString, npgsqlOptions =>
                    npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "identity"))
                .Options;

            return new IdentityDbContext(options, new TenantContext());
        }

        private static PropertyManagementDbContext CreatePropertyManagementDbContext(string connectionString)
        {
            var options = new DbContextOptionsBuilder<PropertyManagementDbContext>()
                .UseNpgsql(connectionString, npgsqlOptions =>
                    npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "property_management"))
                .Options;

            return new PropertyManagementDbContext(options, new TenantContext());
        }

        private async Task ProvisionOutboxAsMigratorAsync(string schema, Type dbContextType)
        {
            var hostBuilder = Host.CreateApplicationBuilder();
            hostBuilder.UseWolverine(opts =>
            {
                opts.EnrollAncillaryPostgresqlOutbox(MigratorConnectionString, schema, dbContextType);
                opts.AutoBuildMessageStorageOnStartup = AutoCreate.None;
                opts.UseEntityFrameworkCoreTransactions();
            });

            using var outboxHost = hostBuilder.Build();
            await outboxHost.SetupResources();

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

    private async Task<IHost> BuildHostAsync(Action<IServiceCollection>? overrides = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Identity"] = _appConnectionString,
            ["ConnectionStrings:PropertyManagement"] = _appConnectionString,
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
                    services.AddControllers().AddApplicationPart(typeof(PropertiesController).Assembly);
                    services.AddScoped<ITenantContext, TenantContext>();
                    services.AddScoped<ICurrentTenantProvider, TenantContextCurrentTenantProvider>();
                    services.AddIHostProTenantAwarePipeline();

                    services.AddIdentityModule(configuration, isDevelopmentEnvironment: false);
                    services.AddIdentityJwtIssuance(configuration);
                    services.AddIdentityJwtBearerAuthentication();
                    services.AddIdentityAuthorization();

                    services.AddPropertyManagementModule(configuration);
                    services.AddPropertyManagementCommandDispatch();

                    overrides?.Invoke(services);
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
                opts.EnrollAncillaryPostgresqlOutbox(_appConnectionString, PropertyManagementOutboxSchema, typeof(PropertyManagementDbContext));
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

    private static readonly AddressRequest ValidAddress = new(
        "59090-000", "Rua Exemplo", "100", "Bloco A", "Ponta Negra", "Natal", "RN", "BR");

    // ---- HTTP helpers ---------------------------------------------------

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string route, object? body, string? token) =>
        SendAsync(client, HttpMethod.Post, route, body, token);

    private static Task<HttpResponseMessage> PatchAsync(HttpClient client, string route, object body, string? token) =>
        SendAsync(client, HttpMethod.Patch, route, body, token);

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string route, object? body, string? token)
    {
        var request = new HttpRequestMessage(method, route);
        if (body is not null)
            request.Content = JsonContent.Create(body, options: JsonWebDefaults);
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return await client.SendAsync(request);
    }

    private static async Task<PropertyDetailResponse> CreatePropertyAsync(HttpClient client, string token, string code)
    {
        var response = await PostAsync(client, "/api/v1/properties", new CreatePropertyRequest(code, $"Property {code}", 2, null, ValidAddress), token);
        return (await response.Content.ReadFromJsonAsync<PropertyDetailResponse>(JsonWebDefaults))!;
    }

    private static async Task<PropertyDetailResponse> CreateActivePropertyAsync(HttpClient client, string token, string code)
    {
        var created = await CreatePropertyAsync(client, token, code);
        var response = await PostAsync(client, $"/api/v1/properties/{created.Id}/activate", null, token);
        return (await response.Content.ReadFromJsonAsync<PropertyDetailResponse>(JsonWebDefaults))!;
    }

    private static async Task<PropertyDetailResponse> CreateInactivePropertyAsync(HttpClient client, string token, string code)
    {
        var active = await CreateActivePropertyAsync(client, token, code);
        var response = await PostAsync(client, $"/api/v1/properties/{active.Id}/deactivate", null, token);
        return (await response.Content.ReadFromJsonAsync<PropertyDetailResponse>(JsonWebDefaults))!;
    }

    private static async Task<PropertyDetailResponse> CreateArchivedPropertyAsync(HttpClient client, string token, string code)
    {
        var created = await CreatePropertyAsync(client, token, code);
        var response = await PostAsync(client, $"/api/v1/properties/{created.Id}/archive", null, token);
        return (await response.Content.ReadFromJsonAsync<PropertyDetailResponse>(JsonWebDefaults))!;
    }

    // ---- Tests: authentication/authorization ---------------------------

    [Fact]
    public async Task Activate_without_a_token_returns_401()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();

        var response = await PostAsync(client, $"/api/v1/properties/{Guid.NewGuid()}/activate", null, token: null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Activate_with_a_role_lacking_PROPERTIES_MANAGE_returns_403()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), Guid.NewGuid(), ["HOUSEKEEPER"]);

        var response = await PostAsync(client, $"/api/v1/properties/{Guid.NewGuid()}/activate", null, token);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ---- Tests: Activate --------------------------------------------------

    [Fact]
    public async Task Activate_a_draft_property_returns_200_with_no_store()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), Guid.NewGuid(), ["ADMIN"]);
        var created = await CreatePropertyAsync(client, token, "LHT-1");

        var response = await PostAsync(client, $"/api/v1/properties/{created.Id}/activate", null, token);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        var body = await response.Content.ReadFromJsonAsync<PropertyDetailResponse>(JsonWebDefaults);
        body!.Status.Should().Be("active");
    }

    [Fact]
    public async Task Activate_an_inactive_property_returns_200()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), Guid.NewGuid(), ["ADMIN"]);
        var inactive = await CreateInactivePropertyAsync(client, token, "LHT-2");

        var response = await PostAsync(client, $"/api/v1/properties/{inactive.Id}/activate", null, token);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Activate_an_already_active_property_returns_409()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), Guid.NewGuid(), ["ADMIN"]);
        var active = await CreateActivePropertyAsync(client, token, "LHT-3");

        var response = await PostAsync(client, $"/api/v1/properties/{active.Id}/activate", null, token);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Activate_an_archived_property_returns_409()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), Guid.NewGuid(), ["ADMIN"]);
        var archived = await CreateArchivedPropertyAsync(client, token, "LHT-4");

        var response = await PostAsync(client, $"/api/v1/properties/{archived.Id}/activate", null, token);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    /// <summary>
    /// A genuine "no effective address at all" HTTP scenario is not
    /// reachable through the approved API surface: Create rejects a
    /// property with neither an own address nor a condominium (400), and
    /// Update rejects any combination that would remove the last address
    /// source (400) — both already covered by Checkpoint 3's own HTTP tests.
    /// This test instead confirms the positive counterpart specific to
    /// Activate: a property whose only address source is its condominium
    /// still activates successfully (Checkpoint 4 plan, item 5), proving the
    /// 400 <c>PropertyAddressRequired</c> branch genuinely is defensive/
    /// unreachable rather than silently broken.
    /// </summary>
    [Fact]
    public async Task Activate_a_property_whose_only_address_source_is_its_condominium_succeeds()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), Guid.NewGuid(), ["ADMIN"]);
        var condominiumResponse = await PostAsync(
            client, "/api/v1/condominiums", new CreateCondominiumRequest("Condomínio Exemplo", ValidAddress), token);
        var condominium = await condominiumResponse.Content.ReadFromJsonAsync<CondominiumDetailResponse>(JsonWebDefaults);
        var createResponse = await PostAsync(
            client, "/api/v1/properties", new CreatePropertyRequest("LHT-5", "Property LHT-5", 2, condominium!.Id, null), token);
        var created = await createResponse.Content.ReadFromJsonAsync<PropertyDetailResponse>(JsonWebDefaults);

        var response = await PostAsync(client, $"/api/v1/properties/{created!.Id}/activate", null, token);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<PropertyDetailResponse>(JsonWebDefaults);
        body!.Status.Should().Be("active");
    }

    [Fact]
    public async Task Activate_for_a_nonexistent_property_returns_404()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), Guid.NewGuid(), ["ADMIN"]);

        var response = await PostAsync(client, $"/api/v1/properties/{Guid.NewGuid()}/activate", null, token);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Activate_cross_tenant_returns_404()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var tokenA = await GenerateTokenAsync(host, Guid.NewGuid(), tenantA, ["ADMIN"]);
        var tokenB = await GenerateTokenAsync(host, Guid.NewGuid(), tenantB, ["ADMIN"]);
        var created = await CreatePropertyAsync(client, tokenA, "LHT-6");

        var response = await PostAsync(client, $"/api/v1/properties/{created.Id}/activate", null, tokenB);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ---- Tests: Deactivate --------------------------------------------------

    [Fact]
    public async Task Deactivate_an_active_property_returns_200()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), Guid.NewGuid(), ["ADMIN"]);
        var active = await CreateActivePropertyAsync(client, token, "LHT-7");

        var response = await PostAsync(client, $"/api/v1/properties/{active.Id}/deactivate", null, token);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<PropertyDetailResponse>(JsonWebDefaults);
        body!.Status.Should().Be("inactive");
    }

    [Fact]
    public async Task Deactivate_a_draft_property_returns_409()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), Guid.NewGuid(), ["ADMIN"]);
        var created = await CreatePropertyAsync(client, token, "LHT-8");

        var response = await PostAsync(client, $"/api/v1/properties/{created.Id}/deactivate", null, token);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Deactivate_an_already_inactive_property_returns_409()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), Guid.NewGuid(), ["ADMIN"]);
        var inactive = await CreateInactivePropertyAsync(client, token, "LHT-9");

        var response = await PostAsync(client, $"/api/v1/properties/{inactive.Id}/deactivate", null, token);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    // ---- Tests: Archive -----------------------------------------------------

    [Fact]
    public async Task Archive_a_draft_property_returns_200()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), Guid.NewGuid(), ["ADMIN"]);
        var created = await CreatePropertyAsync(client, token, "LHT-10");

        var response = await PostAsync(client, $"/api/v1/properties/{created.Id}/archive", null, token);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<PropertyDetailResponse>(JsonWebDefaults);
        body!.Status.Should().Be("archived");
    }

    [Fact]
    public async Task Archive_an_inactive_property_returns_200()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), Guid.NewGuid(), ["ADMIN"]);
        var inactive = await CreateInactivePropertyAsync(client, token, "LHT-11");

        var response = await PostAsync(client, $"/api/v1/properties/{inactive.Id}/archive", null, token);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Archive_an_active_property_returns_409()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), Guid.NewGuid(), ["ADMIN"]);
        var active = await CreateActivePropertyAsync(client, token, "LHT-12");

        var response = await PostAsync(client, $"/api/v1/properties/{active.Id}/archive", null, token);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Archive_an_already_archived_property_returns_409()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), Guid.NewGuid(), ["ADMIN"]);
        var archived = await CreateArchivedPropertyAsync(client, token, "LHT-13");

        var response = await PostAsync(client, $"/api/v1/properties/{archived.Id}/archive", null, token);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    // ---- Tests: archived blocks cadastral update ---------------------------

    [Fact]
    public async Task PATCH_of_an_archived_property_returns_409()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), Guid.NewGuid(), ["ADMIN"]);
        var archived = await CreateArchivedPropertyAsync(client, token, "LHT-14");

        var response = await PatchAsync(client, $"/api/v1/properties/{archived.Id}", new { name = "New Name" }, token);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    // ---- Tests: no body, no client-controlled fields -----------------------

    [Fact]
    public async Task Activate_ignores_any_body_sent_including_tenantId_actorId_and_status()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var tenantId = Guid.NewGuid();
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), tenantId, ["ADMIN"]);
        var created = await CreatePropertyAsync(client, token, "LHT-15");

        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/properties/{created.Id}/activate");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = JsonContent.Create(new
        {
            status = "archived",
            tenantId = Guid.NewGuid(),
            actorId = Guid.NewGuid(),
            reason = "some reason",
        }, options: JsonWebDefaults);

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<PropertyDetailResponse>(JsonWebDefaults);
        body!.Status.Should().Be("active"); // never "archived", despite the client's attempt
    }

    // ---- Concurrency ----------------------------------------------------------

    [Fact]
    public async Task Two_concurrent_activations_of_the_same_property_allow_only_one_to_succeed_with_409()
    {
        var barrier = new Barrier(2);
        using var hostA = await BuildHostAsync(overrides: sc =>
            sc.AddScoped<IPropertyAuditWriter>(_ => new BarrierPropertyAuditWriter(barrier)));
        using var hostB = await BuildHostAsync(overrides: sc =>
            sc.AddScoped<IPropertyAuditWriter>(_ => new BarrierPropertyAuditWriter(barrier)));

        using var seedClient = hostA.GetTestClient();
        var token = await GenerateTokenAsync(hostA, Guid.NewGuid(), Guid.NewGuid(), ["ADMIN"]);
        // hostB must accept the same JWT — issued with the same signing key
        // configuration (both hosts built from the same fixture-scoped PEM).
        var created = await CreatePropertyAsync(seedClient, token, "LHT-16");

        using var clientA = hostA.GetTestClient();
        using var clientB = hostB.GetTestClient();

        var taskA = PostAsync(clientA, $"/api/v1/properties/{created.Id}/activate", null, token);
        var taskB = PostAsync(clientB, $"/api/v1/properties/{created.Id}/activate", null, token);
        var responses = await Task.WhenAll(taskA, taskB);

        responses.Count(r => r.StatusCode == HttpStatusCode.OK).Should().Be(1);
        responses.Count(r => r.StatusCode == HttpStatusCode.Conflict).Should().Be(1);

        barrier.Dispose();
    }

    private sealed class BarrierPropertyAuditWriter : IPropertyAuditWriter
    {
        private readonly Barrier _barrier;

        public BarrierPropertyAuditWriter(Barrier barrier) => _barrier = barrier;

        public void Record(PropertyAuditEntry entry) => _barrier.SignalAndWait(TimeSpan.FromSeconds(10));
    }
}
