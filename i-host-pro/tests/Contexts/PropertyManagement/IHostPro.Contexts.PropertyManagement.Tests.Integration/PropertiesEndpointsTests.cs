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
/// End-to-end test of <see cref="PropertiesController"/> against the REAL
/// composition root wiring — mirrors <c>CondominiumsEndpointsTests</c>
/// exactly: real ASP.NET Core host/TestServer, real JWT, real PostgreSQL for
/// both Identity (permission catalog resolution) and Property Management. No
/// RabbitMQ (Checkpoint 3 plan, item 19).
///
/// Presence-aware PATCH semantics (omitted vs. explicit null vs. supplied)
/// can only be exercised with genuinely raw JSON — an <c>UpdatePropertyRequest</c>
/// instance sent through its own <c>OptionalJsonConverter</c> would
/// round-trip an unset field back out as an explicit <c>null</c>, erasing
/// the very distinction under test. Every Update request in this file is
/// therefore a plain anonymous object, whose declared properties are exactly
/// the JSON properties actually sent.
/// </summary>
public class PropertiesEndpointsTests : IClassFixture<PropertiesEndpointsTests.Fixture>
{
    private const string IdentityOutboxSchema = "identity_messaging";
    private const string PropertyManagementOutboxSchema = "property_management_messaging";
    private const string Issuer = "https://identity.ihostpro.test";
    private const string Audience = "ihostpro-api-test";

    private static readonly JsonSerializerOptions JsonWebDefaults = new(JsonSerializerDefaults.Web);

    private readonly string _migratorConnectionString;
    private readonly string _appConnectionString;
    private readonly string _signingKeyPem;

    public PropertiesEndpointsTests(Fixture fixture)
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

                    services.AddPropertyManagementModule(configuration, isDevelopmentEnvironment: false);
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

    private static readonly AddressRequest ValidCondominiumAddress = new(
        "59090-200", "Rua do Condomínio", "1", null, "Ponta Negra", "Natal", "RN", "BR");

    // ---- HTTP helpers ---------------------------------------------------

    private static Task<HttpResponseMessage> GetAsync(HttpClient client, string route, string? token) =>
        SendAsync(client, HttpMethod.Get, route, null, token);

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

    private static async Task<Guid> CreateCondominiumAsync(HttpClient client, string token, string name = "Condomínio Exemplo")
    {
        var response = await PostAsync(client, "/api/v1/condominiums", new CreateCondominiumRequest(name, ValidCondominiumAddress), token);
        var body = await response.Content.ReadFromJsonAsync<CondominiumDetailResponse>(JsonWebDefaults);
        return body!.Id;
    }

    private static async Task<PropertyDetailResponse> CreatePropertyAsync(
        HttpClient client, string token, string code, Guid? condominiumId = null, AddressRequest? address = null)
    {
        var response = await PostAsync(
            client, "/api/v1/properties", new CreatePropertyRequest(code, $"Property {code}", 2, condominiumId, address ?? (condominiumId is null ? ValidAddress : null)), token);
        return (await response.Content.ReadFromJsonAsync<PropertyDetailResponse>(JsonWebDefaults))!;
    }

    // ---- Tests: authentication/authorization ---------------------------

    [Fact]
    public async Task Create_without_a_token_returns_401()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();

        var response = await PostAsync(client, "/api/v1/properties", new CreatePropertyRequest("STUDIO-1", "Studio 1", 2, null, ValidAddress), token: null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Create_with_a_role_lacking_PROPERTIES_MANAGE_returns_403()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), Guid.NewGuid(), ["HOUSEKEEPER"]);

        var response = await PostAsync(client, "/api/v1/properties", new CreatePropertyRequest("STUDIO-1", "Studio 1", 2, null, ValidAddress), token);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ---- Tests: Create ----------------------------------------------------

    [Fact]
    public async Task Create_with_own_address_returns_201_with_Location_and_no_store_and_the_full_body_shape()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), Guid.NewGuid(), ["ADMIN"]);

        var response = await PostAsync(client, "/api/v1/properties", new CreatePropertyRequest("STUDIO-1", "Studio 1", 2, null, ValidAddress), token);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        response.Headers.Location.Should().NotBeNull();
        response.Headers.CacheControl!.NoStore.Should().BeTrue();

        var body = await response.Content.ReadFromJsonAsync<PropertyDetailResponse>(JsonWebDefaults);
        body!.Code.Should().Be("STUDIO-1");
        body.Name.Should().Be("Studio 1");
        body.Capacity.Should().Be(2);
        body.Status.Should().Be("draft");
        body.CondominiumId.Should().BeNull();
        body.Address.Should().NotBeNull();
        body.EffectiveAddress.Should().NotBeNull();
        body.EffectiveAddressSource.Should().Be("property");
    }

    [Fact]
    public async Task Create_with_a_condominium_and_no_own_address_returns_201_with_effectiveAddressSource_condominium()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), Guid.NewGuid(), ["ADMIN"]);
        var condominiumId = await CreateCondominiumAsync(client, token);

        var response = await PostAsync(client, "/api/v1/properties", new CreatePropertyRequest("STUDIO-2", "Studio 2", 2, condominiumId, null), token);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<PropertyDetailResponse>(JsonWebDefaults);
        body!.Address.Should().BeNull();
        body.EffectiveAddressSource.Should().Be("condominium");
        body.CondominiumId.Should().Be(condominiumId);
    }

    [Fact]
    public async Task Create_without_fields_returns_400()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), Guid.NewGuid(), ["ADMIN"]);

        var response = await PostAsync(client, "/api/v1/properties", new CreatePropertyRequest(null, null, null, null, null), token);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Create_with_neither_condominium_nor_address_returns_400()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), Guid.NewGuid(), ["ADMIN"]);

        var response = await PostAsync(client, "/api/v1/properties", new CreatePropertyRequest("STUDIO-3", "Studio 3", 2, null, null), token);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Create_referencing_a_nonexistent_condominium_returns_404()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), Guid.NewGuid(), ["ADMIN"]);

        var response = await PostAsync(client, "/api/v1/properties", new CreatePropertyRequest("STUDIO-4", "Studio 4", 2, Guid.NewGuid(), null), token);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Create_with_a_duplicate_code_in_the_same_tenant_returns_409()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), Guid.NewGuid(), ["ADMIN"]);
        await PostAsync(client, "/api/v1/properties", new CreatePropertyRequest("STUDIO-5", "First", 2, null, ValidAddress), token);

        var response = await PostAsync(client, "/api/v1/properties", new CreatePropertyRequest("studio-5", "Second", 2, null, ValidAddress), token);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    // ---- Tests: List/Detail -----------------------------------------------

    [Fact]
    public async Task List_returns_200_with_only_the_authenticated_tenants_properties_and_never_returns_address()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var tokenA = await GenerateTokenAsync(host, Guid.NewGuid(), tenantA, ["ADMIN"]);
        var tokenB = await GenerateTokenAsync(host, Guid.NewGuid(), tenantB, ["ADMIN"]);
        await PostAsync(client, "/api/v1/properties", new CreatePropertyRequest("STUDIO-6", "Tenant A's", 2, null, ValidAddress), tokenA);
        await PostAsync(client, "/api/v1/properties", new CreatePropertyRequest("STUDIO-6", "Tenant B's", 2, null, ValidAddress), tokenB);

        var response = await GetAsync(client, "/api/v1/properties?page=1&pageSize=20", tokenA);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var rawBody = await response.Content.ReadAsStringAsync();
        rawBody.Should().NotContain("address", "the listing must never carry own or effective address");

        var body = await response.Content.ReadFromJsonAsync<PagedPropertyResponse>(JsonWebDefaults);
        body!.Items.Should().ContainSingle(p => p.Name == "Tenant A's");
        body.Items.Should().NotContain(p => p.Name == "Tenant B's");
    }

    [Fact]
    public async Task GetById_for_an_existing_property_returns_200_with_effective_address()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), Guid.NewGuid(), ["ADMIN"]);
        var created = await CreatePropertyAsync(client, token, "STUDIO-7");

        var response = await GetAsync(client, $"/api/v1/properties/{created.Id}", token);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<PropertyDetailResponse>(JsonWebDefaults);
        body!.EffectiveAddress.Should().NotBeNull();
    }

    [Fact]
    public async Task GetById_for_a_nonexistent_property_returns_404()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), Guid.NewGuid(), ["ADMIN"]);

        var response = await GetAsync(client, $"/api/v1/properties/{Guid.NewGuid()}", token);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetById_cross_tenant_returns_404()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var tokenA = await GenerateTokenAsync(host, Guid.NewGuid(), tenantA, ["ADMIN"]);
        var tokenB = await GenerateTokenAsync(host, Guid.NewGuid(), tenantB, ["ADMIN"]);
        var created = await CreatePropertyAsync(client, tokenA, "STUDIO-8");

        var response = await GetAsync(client, $"/api/v1/properties/{created.Id}", tokenB);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ---- Tests: Update — basics ---------------------------------------------

    [Fact]
    public async Task Update_with_valid_data_returns_200()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), Guid.NewGuid(), ["ADMIN"]);
        var created = await CreatePropertyAsync(client, token, "STUDIO-9");

        var response = await PatchAsync(client, $"/api/v1/properties/{created.Id}", new { name = "Updated" }, token);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<PropertyDetailResponse>(JsonWebDefaults);
        body!.Name.Should().Be("Updated");
    }

    [Fact]
    public async Task Update_without_fields_returns_400()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), Guid.NewGuid(), ["ADMIN"]);
        var created = await CreatePropertyAsync(client, token, "STUDIO-10");

        var response = await PatchAsync(client, $"/api/v1/properties/{created.Id}", new { }, token);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Update_of_a_nonexistent_property_returns_404()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), Guid.NewGuid(), ["ADMIN"]);

        var response = await PatchAsync(client, $"/api/v1/properties/{Guid.NewGuid()}", new { name = "X" }, token);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_no_op_update_returns_200_without_error()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), Guid.NewGuid(), ["ADMIN"]);
        var created = await CreatePropertyAsync(client, token, "STUDIO-11");

        var response = await PatchAsync(client, $"/api/v1/properties/{created.Id}", new { name = created.Name }, token);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Update_with_a_duplicate_code_returns_409()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), Guid.NewGuid(), ["ADMIN"]);
        await CreatePropertyAsync(client, token, "STUDIO-12");
        var second = await CreatePropertyAsync(client, token, "STUDIO-13");

        var response = await PatchAsync(client, $"/api/v1/properties/{second.Id}", new { code = "studio-12" }, token);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    // ---- Tests: Update — omitted vs. null vs. supplied ------------------------

    [Fact]
    public async Task Omitting_a_field_in_the_PATCH_body_leaves_it_unchanged()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), Guid.NewGuid(), ["ADMIN"]);
        var created = await CreatePropertyAsync(client, token, "STUDIO-14");

        // Only "name" is present in the JSON body — capacity/condominiumId/address/code are genuinely omitted.
        var response = await PatchAsync(client, $"/api/v1/properties/{created.Id}", new { name = "Renamed" }, token);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<PropertyDetailResponse>(JsonWebDefaults);
        body!.Name.Should().Be("Renamed");
        body.Code.Should().Be(created.Code);
        body.Capacity.Should().Be(created.Capacity);
        body.Address!.ZipCode.Should().Be(created.Address!.ZipCode);
    }

    [Fact]
    public async Task Explicit_null_address_removes_the_own_address_and_falls_back_to_the_condominium()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), Guid.NewGuid(), ["ADMIN"]);
        var condominiumId = await CreateCondominiumAsync(client, token);
        var created = await CreatePropertyAsync(client, token, "STUDIO-15", condominiumId, ValidAddress);

        var response = await PatchAsync(client, $"/api/v1/properties/{created.Id}", new { address = (object?)null }, token);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<PropertyDetailResponse>(JsonWebDefaults);
        body!.Address.Should().BeNull();
        body.EffectiveAddressSource.Should().Be("condominium");
    }

    [Fact]
    public async Task Explicit_null_address_without_a_condominium_returns_400()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), Guid.NewGuid(), ["ADMIN"]);
        var created = await CreatePropertyAsync(client, token, "STUDIO-16");

        var response = await PatchAsync(client, $"/api/v1/properties/{created.Id}", new { address = (object?)null }, token);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Explicit_null_condominiumId_without_an_own_address_returns_400()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), Guid.NewGuid(), ["ADMIN"]);
        var condominiumId = await CreateCondominiumAsync(client, token);
        var created = await CreatePropertyAsync(client, token, "STUDIO-17", condominiumId, null);

        var response = await PatchAsync(client, $"/api/v1/properties/{created.Id}", new { condominiumId = (Guid?)null }, token);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Removing_the_condominium_link_while_supplying_an_own_address_in_the_same_request_returns_200()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), Guid.NewGuid(), ["ADMIN"]);
        var condominiumId = await CreateCondominiumAsync(client, token);
        var created = await CreatePropertyAsync(client, token, "STUDIO-18", condominiumId, null);

        var response = await PatchAsync(
            client, $"/api/v1/properties/{created.Id}",
            new { condominiumId = (Guid?)null, address = ValidAddress }, token);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<PropertyDetailResponse>(JsonWebDefaults);
        body!.CondominiumId.Should().BeNull();
        body.EffectiveAddressSource.Should().Be("property");
    }

    [Fact]
    public async Task Supplying_a_new_condominiumId_reassigns_the_link()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), Guid.NewGuid(), ["ADMIN"]);
        var condominiumId = await CreateCondominiumAsync(client, token, "New Condo");
        var created = await CreatePropertyAsync(client, token, "STUDIO-19");

        var response = await PatchAsync(client, $"/api/v1/properties/{created.Id}", new { condominiumId }, token);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<PropertyDetailResponse>(JsonWebDefaults);
        body!.CondominiumId.Should().Be(condominiumId);
    }

    // ---- Tests: client cannot set forbidden fields -----------------------------

    [Fact]
    public async Task Requests_do_not_accept_tenantId_actorId_or_status_from_the_client()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var tenantId = Guid.NewGuid();
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), tenantId, ["ADMIN"]);

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/properties");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = JsonContent.Create(new
        {
            code = "STUDIO-20",
            name = "X",
            capacity = 2,
            address = ValidAddress,
            status = "active",
            tenantId = Guid.NewGuid(),
            actorId = Guid.NewGuid(),
            ownerUserId = Guid.NewGuid(),
        }, options: JsonWebDefaults);

        var response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var body = await response.Content.ReadFromJsonAsync<PropertyDetailResponse>(JsonWebDefaults);
        body!.Status.Should().Be("draft"); // never "active", despite the client's attempt

        var listResponse = await GetAsync(client, "/api/v1/properties", token);
        var listBody = await listResponse.Content.ReadFromJsonAsync<PagedPropertyResponse>(JsonWebDefaults);
        listBody!.Items.Should().Contain(p => p.Id == body.Id); // proves the real JWT tenant was used, not the ignored body value
    }

    // ---- Concurrency ----------------------------------------------------------

    /// <summary>
    /// Two plain <c>Task.WhenAll</c>'d HTTP requests against a single
    /// in-memory <c>TestServer</c>, without further synchronization, are not
    /// reliably concurrent at the database level — confirmed empirically:
    /// this raced to a false negative (both 200) every time it was tried
    /// without a barrier, the same underlying non-determinism
    /// <c>CondominiumsEndpointsTests</c>' own equivalent test also exhibits
    /// (pre-existing, out of this checkpoint's scope to touch). Fixed here by
    /// reusing the exact deterministic technique already proven in
    /// <c>PropertyCommandHandlerTests</c>: two SEPARATE hosts, each with an
    /// <see cref="IPropertyAuditWriter"/> override that blocks on the SAME
    /// shared <see cref="Barrier"/> until both requests have reached the
    /// audit-write point, guaranteeing their subsequent <c>SaveChanges</c>
    /// calls genuinely race on the row's <c>xmin</c> token.
    /// </summary>
    private sealed class BarrierPropertyAuditWriter : IPropertyAuditWriter
    {
        private readonly Barrier _barrier;

        public BarrierPropertyAuditWriter(Barrier barrier) => _barrier = barrier;

        public void Record(PropertyAuditEntry entry) => _barrier.SignalAndWait(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Two_concurrent_updates_of_the_same_property_allow_only_one_to_succeed_with_409()
    {
        using var seedHost = await BuildHostAsync();
        using var seedClient = seedHost.GetTestClient();
        var tenantId = Guid.NewGuid();
        var token = await GenerateTokenAsync(seedHost, Guid.NewGuid(), tenantId, ["ADMIN"]);
        var created = await CreatePropertyAsync(seedClient, token, "STUDIO-21");

        using var barrier = new Barrier(2);
        using var hostA = await BuildHostAsync(overrides: sc =>
            sc.AddScoped<IPropertyAuditWriter>(_ => new BarrierPropertyAuditWriter(barrier)));
        using var hostB = await BuildHostAsync(overrides: sc =>
            sc.AddScoped<IPropertyAuditWriter>(_ => new BarrierPropertyAuditWriter(barrier)));
        using var clientA = hostA.GetTestClient();
        using var clientB = hostB.GetTestClient();

        var taskA = PatchAsync(clientA, $"/api/v1/properties/{created.Id}", new { name = "Name A" }, token);
        var taskB = PatchAsync(clientB, $"/api/v1/properties/{created.Id}", new { name = "Name B" }, token);
        var responses = await Task.WhenAll(taskA, taskB);

        responses.Count(r => r.StatusCode == HttpStatusCode.OK).Should().Be(1);
        responses.Count(r => r.StatusCode == HttpStatusCode.Conflict).Should().Be(1);
    }
}
