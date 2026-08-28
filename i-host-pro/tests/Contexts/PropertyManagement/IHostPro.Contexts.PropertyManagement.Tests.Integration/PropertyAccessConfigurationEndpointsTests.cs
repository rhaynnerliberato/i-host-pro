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
/// Fase 10, Checkpoint 6.2 (Guest Access Secure Delivery Corrective
/// Implementation) — authentication/authorization proof for
/// <see cref="PropertyAccessConfigurationController"/> against the REAL
/// composition root wiring — mirrors <c>FrontDeskContactsEndpointsTests</c>'s
/// own fixture exactly (Postgres only, no RabbitMQ; real JWT
/// issuance/validation; real seeded permission catalog).
/// </summary>
public class PropertyAccessConfigurationEndpointsTests : IClassFixture<PropertyAccessConfigurationEndpointsTests.Fixture>
{
    private const string IdentityOutboxSchema = "identity_messaging";
    private const string PropertyManagementOutboxSchema = "property_management_messaging";
    private const string Issuer = "https://identity.ihostpro.test";
    private const string Audience = "ihostpro-api-test";

    private static readonly JsonSerializerOptions JsonWebDefaults = new(JsonSerializerDefaults.Web);

    private readonly string _appConnectionString;
    private readonly string _signingKeyPem;

    public PropertyAccessConfigurationEndpointsTests(Fixture fixture)
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
            await using (var propertyManagementDbContext = CreatePropertyManagementDbContext(MigratorConnectionString))
                await propertyManagementDbContext.Database.MigrateAsync();

            await ProvisionOutboxAsMigratorAsync(IdentityOutboxSchema, typeof(IdentityDbContext));
            await ProvisionOutboxAsMigratorAsync(PropertyManagementOutboxSchema, typeof(PropertyManagementDbContext));
        }

        public async Task DisposeAsync() => await _container.DisposeAsync();

        private static IdentityDbContext CreateIdentityDbContext(string connectionString)
        {
            var options = new DbContextOptionsBuilder<IdentityDbContext>()
                .UseNpgsql(connectionString, npgsqlOptions => npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "identity"))
                .Options;
            return new IdentityDbContext(options, new TenantContext());
        }

        private static PropertyManagementDbContext CreatePropertyManagementDbContext(string connectionString)
        {
            var options = new DbContextOptionsBuilder<PropertyManagementDbContext>()
                .UseNpgsql(connectionString, npgsqlOptions => npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "property_management"))
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

    private async Task<IHost> BuildHostAsync()
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
                    services.AddControllers().AddApplicationPart(typeof(CondominiumsController).Assembly);
                    services.AddScoped<ITenantContext, TenantContext>();
                    services.AddScoped<ICurrentTenantProvider, TenantContextCurrentTenantProvider>();
                    services.AddIHostProTenantAwarePipeline();

                    services.AddIdentityModule(configuration, isDevelopmentEnvironment: false);
                    services.AddIdentityJwtIssuance(configuration);
                    services.AddIdentityJwtBearerAuthentication();
                    services.AddIdentityAuthorization();

                    services.AddPropertyManagementModule(configuration, isDevelopmentEnvironment: false);
                    services.AddPropertyManagementCommandDispatch();
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
        "59090-000", "Rua Exemplo", "100", null, "Ponta Negra", "Natal", "RN", "BR");

    private static async Task<Guid> CreatePropertyAsync(HttpClient client, string token, string code)
    {
        var response = await PostAsync(
            client, "/api/v1/properties", new CreatePropertyRequest(code, $"Property {code}", 2, null, ValidAddress), token);
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var body = await response.Content.ReadFromJsonAsync<PropertyDetailResponse>(JsonWebDefaults);
        return body!.Id;
    }

    private static Task<HttpResponseMessage> GetAsync(HttpClient client, string route, string? token) =>
        SendAsync(client, HttpMethod.Get, route, null, token);

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string route, object? body, string? token) =>
        SendAsync(client, HttpMethod.Post, route, body, token);

    private static Task<HttpResponseMessage> PutAsync(HttpClient client, string route, object body, string? token) =>
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

    // ---- Tests ------------------------------------------------------------

    [Fact]
    public async Task Set_without_a_token_returns_401()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();

        var response = await PutAsync(client, $"/api/v1/properties/{Guid.NewGuid()}/access-configuration",
            new SetPropertyAccessConfigurationRequest("front-door-code", "Instructions", true), token: null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Set_with_a_role_lacking_PROPERTIES_MANAGE_returns_403()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), Guid.NewGuid(), ["HOUSEKEEPER"]);

        var response = await PutAsync(client, $"/api/v1/properties/{Guid.NewGuid()}/access-configuration",
            new SetPropertyAccessConfigurationRequest("front-door-code", "Instructions", true), token);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Set_as_ADMIN_succeeds_and_Get_returns_it_back_never_a_raw_credential()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var tenantId = Guid.NewGuid();
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), tenantId, ["ADMIN"]);
        var propertyId = await CreatePropertyAsync(client, token, "ACCESS-1");

        var setResponse = await PutAsync(client, $"/api/v1/properties/{propertyId}/access-configuration",
            new SetPropertyAccessConfigurationRequest("front-door-code", "Wi-Fi: guest", true), token);

        setResponse.StatusCode.Should().Be(HttpStatusCode.OK, await setResponse.Content.ReadAsStringAsync());
        var setBody = await setResponse.Content.ReadFromJsonAsync<PropertyAccessConfigurationResponse>(JsonWebDefaults);
        setBody!.AccessCredentialSecretReference.Should().Be("front-door-code");
        setBody.AccessInstructions.Should().Be("Wi-Fi: guest");
        setBody.IsActive.Should().BeTrue();

        var getResponse = await GetAsync(client, $"/api/v1/properties/{propertyId}/access-configuration", token);
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var getBody = await getResponse.Content.ReadFromJsonAsync<PropertyAccessConfigurationResponse>(JsonWebDefaults);
        getBody!.Id.Should().Be(setBody.Id);
        getBody.AccessCredentialSecretReference.Should().Be("front-door-code",
            "the endpoint returns the reference — never the raw credential, which this Bounded Context never stores");
    }

    [Fact]
    public async Task Set_for_a_nonexistent_property_returns_404()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), Guid.NewGuid(), ["ADMIN"]);

        var response = await PutAsync(client, $"/api/v1/properties/{Guid.NewGuid()}/access-configuration",
            new SetPropertyAccessConfigurationRequest("front-door-code", "Instructions", true), token);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Get_before_any_configuration_exists_returns_404()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), Guid.NewGuid(), ["ADMIN"]);
        var propertyId = await CreatePropertyAsync(client, token, "ACCESS-2");

        var response = await GetAsync(client, $"/api/v1/properties/{propertyId}/access-configuration", token);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_property_from_another_tenant_is_not_found()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var ownerToken = await GenerateTokenAsync(host, Guid.NewGuid(), Guid.NewGuid(), ["ADMIN"]);
        var propertyId = await CreatePropertyAsync(client, ownerToken, "ACCESS-3");
        await PutAsync(client, $"/api/v1/properties/{propertyId}/access-configuration",
            new SetPropertyAccessConfigurationRequest("front-door-code", "Instructions", true), ownerToken);

        var otherTenantToken = await GenerateTokenAsync(host, Guid.NewGuid(), Guid.NewGuid(), ["ADMIN"]);

        var response = await GetAsync(client, $"/api/v1/properties/{propertyId}/access-configuration", otherTenantToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound, "a cross-tenant propertyId must be indistinguishable from a non-existent one (RLS)");
    }

    [Fact]
    public async Task Set_is_idempotent_when_every_field_already_matches()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), Guid.NewGuid(), ["ADMIN"]);
        var propertyId = await CreatePropertyAsync(client, token, "ACCESS-4");

        await PutAsync(client, $"/api/v1/properties/{propertyId}/access-configuration",
            new SetPropertyAccessConfigurationRequest("front-door-code", "Instructions", true), token);
        var secondResponse = await PutAsync(client, $"/api/v1/properties/{propertyId}/access-configuration",
            new SetPropertyAccessConfigurationRequest("front-door-code", "Instructions", true), token);

        secondResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await secondResponse.Content.ReadFromJsonAsync<PropertyAccessConfigurationResponse>(JsonWebDefaults);
        body!.AccessCredentialSecretReference.Should().Be("front-door-code");
    }
}
