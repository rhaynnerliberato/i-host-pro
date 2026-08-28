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
using IHostPro.Contexts.Identity.Contracts.Authorization;
using IHostPro.Contexts.Identity.Domain;
using IHostPro.Contexts.Identity.Domain.ValueObjects;
using IHostPro.Contexts.Identity.Infrastructure;
using IHostPro.Contexts.Identity.Infrastructure.Authentication;
using IHostPro.Contexts.Identity.Infrastructure.Persistence;
using IHostPro.Contexts.Identity.Infrastructure.Security;
using IHostPro.Contexts.PropertyManagement.Api.Contracts;
using IHostPro.Contexts.PropertyManagement.Api.Controllers;
using IHostPro.Contexts.PropertyManagement.Application;
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
using Microsoft.Extensions.Options;
using Npgsql;
using Testcontainers.PostgreSql;
using Wolverine;
using Wolverine.EntityFrameworkCore;

namespace IHostPro.Contexts.PropertyManagement.Tests.Integration;

/// <summary>
/// End-to-end test of the five Ownership actions
/// (<see cref="PropertiesController.LinkOwner"/>/<see cref="PropertiesController.UnlinkOwner"/>/
/// <see cref="PropertiesController.ListOwners"/>/<see cref="MyPropertiesController.List"/>/
/// <see cref="MyPropertiesController.GetById"/>) against the REAL composition
/// root wiring — mirrors <c>PropertiesLifecycleEndpointsTests</c> exactly:
/// real ASP.NET Core host/TestServer, real JWT, real PostgreSQL (both
/// <c>identity</c> and <c>property_management</c> schemas). No RabbitMQ
/// (Checkpoint 5 plan, item 17).
/// </summary>
public class PropertyOwnerEndpointsTests : IClassFixture<PropertyOwnerEndpointsTests.Fixture>
{
    private const string IdentityOutboxSchema = "identity_messaging";
    private const string PropertyManagementOutboxSchema = "property_management_messaging";
    private const string Issuer = "https://identity.ihostpro.test";
    private const string Audience = "ihostpro-api-test";
    private const string KnownPassword = "Correct-Horse-Battery-Staple-42!";

    private static readonly JsonSerializerOptions JsonWebDefaults = new(JsonSerializerDefaults.Web);

    private readonly string _migratorConnectionString;
    private readonly string _appConnectionString;
    private readonly string _signingKeyPem;

    public PropertyOwnerEndpointsTests(Fixture fixture)
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

    // ---- HTTP helpers ---------------------------------------------------

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string route, object? body, string? token) =>
        SendAsync(client, HttpMethod.Post, route, body, token);

    private static Task<HttpResponseMessage> DeleteAsync(HttpClient client, string route, string? token) =>
        SendAsync(client, HttpMethod.Delete, route, null, token);

    private static Task<HttpResponseMessage> GetAsync(HttpClient client, string route, string? token) =>
        SendAsync(client, HttpMethod.Get, route, null, token);

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string route, object? body, string? token)
    {
        var request = new HttpRequestMessage(method, route);
        if (body is not null)
            request.Content = JsonContent.Create(body, options: JsonWebDefaults);
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return await client.SendAsync(request);
    }

    private static async Task<PropertyDetailResponse> CreatePropertyAsync(HttpClient client, string adminToken, string code)
    {
        var response = await PostAsync(client, "/api/v1/properties", new CreatePropertyRequest(code, $"Property {code}", 2, null, ValidAddress), adminToken);
        return (await response.Content.ReadFromJsonAsync<PropertyDetailResponse>(JsonWebDefaults))!;
    }

    // ---- Seeding: Identity (tenant + eligible/ineligible owners) -----------

    private async Task<Guid> SeedTenantAsync()
    {
        var tenantId = Guid.NewGuid();
        await using var dbContext = CreateIdentityDbContext(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);

        var tenant = Tenant.Provision(
            tenantId, TenantSlug.Create($"tenant-{Guid.NewGuid():N}"[..20]), "Test Tenant", DateTimeOffset.UtcNow);
        dbContext.Tenants.Add(tenant);

        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();

        return tenantId;
    }

    private async Task<Guid> SeedIdentityUserAsync(Guid tenantId, bool blocked = false, string[]? roleCodes = null)
    {
        await using var dbContext = CreateIdentityDbContext(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);

        var hasher = new Argon2PasswordHasher(new KonsciousArgon2idPrimitive(), Options.Create(new Argon2Options()));
        var hash = PasswordHash.FromEncoded(hasher.HashPassword(null!, KnownPassword));
        var now = DateTimeOffset.UtcNow;
        var user = User.Register(Guid.NewGuid(), tenantId, Email.Create($"{Guid.NewGuid():N}@ihostpro.com"), "Test Owner", hash, now);
        if (blocked)
            user.Block(now);
        dbContext.Users.Add(user);

        foreach (var roleCode in roleCodes ?? [])
            dbContext.UserRoles.Add(new UserRole(tenantId, user.Id, roleCode, now, assignedByUserId: null));

        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();

        return user.Id;
    }

    private async Task<Guid> SeedEligibleOwnerAsync(Guid tenantId) =>
        await SeedIdentityUserAsync(tenantId, roleCodes: [IdentityRoleCodes.PropertyOwner]);

    private IdentityDbContext CreateIdentityDbContext(Guid tenantId)
    {
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);
        var options = new DbContextOptionsBuilder<IdentityDbContext>()
            .UseNpgsql(_appConnectionString, npgsqlOptions =>
                npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "identity"))
            .Options;

        return new IdentityDbContext(options, tenantContext);
    }

    private static async Task SetPostgresTenantAsync(IdentityDbContext dbContext, Guid tenantId) =>
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.tenant_id', {tenantId.ToString()}, true)");

    // ---- Tests: authentication/authorization (admin) ---------------------

    [Fact]
    public async Task LinkOwner_without_a_token_returns_401()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();

        var response = await PostAsync(client, $"/api/v1/properties/{Guid.NewGuid()}/owners", new LinkPropertyOwnerRequest(Guid.NewGuid()), token: null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task LinkOwner_with_a_role_lacking_PROPERTIES_MANAGE_returns_403()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), Guid.NewGuid(), ["HOUSEKEEPER"]);

        var response = await PostAsync(client, $"/api/v1/properties/{Guid.NewGuid()}/owners", new LinkPropertyOwnerRequest(Guid.NewGuid()), token);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task UnlinkOwner_without_a_token_returns_401()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();

        var response = await DeleteAsync(client, $"/api/v1/properties/{Guid.NewGuid()}/owners/{Guid.NewGuid()}", token: null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ListOwners_without_a_token_returns_401()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();

        var response = await GetAsync(client, $"/api/v1/properties/{Guid.NewGuid()}/owners", token: null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ---- Tests: LinkOwner ------------------------------------------------

    [Fact]
    public async Task LinkOwner_an_eligible_owner_returns_201_with_no_store_and_a_Location_header()
    {
        var tenantId = await SeedTenantAsync();
        var ownerId = await SeedEligibleOwnerAsync(tenantId);
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var adminToken = await GenerateTokenAsync(host, Guid.NewGuid(), tenantId, ["ADMIN"]);
        var property = await CreatePropertyAsync(client, adminToken, "OHT-1");

        var response = await PostAsync(client, $"/api/v1/properties/{property.Id}/owners", new LinkPropertyOwnerRequest(ownerId), adminToken);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        response.Headers.Location.Should().NotBeNull();
        var body = await response.Content.ReadFromJsonAsync<PropertyOwnerResponse>(JsonWebDefaults);
        body!.PropertyId.Should().Be(property.Id);
        body.OwnerUserId.Should().Be(ownerId);
    }

    [Fact]
    public async Task LinkOwner_for_a_nonexistent_property_returns_404()
    {
        var tenantId = await SeedTenantAsync();
        var ownerId = await SeedEligibleOwnerAsync(tenantId);
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var adminToken = await GenerateTokenAsync(host, Guid.NewGuid(), tenantId, ["ADMIN"]);

        var response = await PostAsync(client, $"/api/v1/properties/{Guid.NewGuid()}/owners", new LinkPropertyOwnerRequest(ownerId), adminToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task LinkOwner_for_a_nonexistent_owner_user_returns_404()
    {
        var tenantId = await SeedTenantAsync();
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var adminToken = await GenerateTokenAsync(host, Guid.NewGuid(), tenantId, ["ADMIN"]);
        var property = await CreatePropertyAsync(client, adminToken, "OHT-2");

        var response = await PostAsync(client, $"/api/v1/properties/{property.Id}/owners", new LinkPropertyOwnerRequest(Guid.NewGuid()), adminToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task LinkOwner_a_blocked_owner_returns_409()
    {
        var tenantId = await SeedTenantAsync();
        var ownerId = await SeedIdentityUserAsync(tenantId, blocked: true, roleCodes: [IdentityRoleCodes.PropertyOwner]);
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var adminToken = await GenerateTokenAsync(host, Guid.NewGuid(), tenantId, ["ADMIN"]);
        var property = await CreatePropertyAsync(client, adminToken, "OHT-3");

        var response = await PostAsync(client, $"/api/v1/properties/{property.Id}/owners", new LinkPropertyOwnerRequest(ownerId), adminToken);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task LinkOwner_a_user_without_the_role_returns_409()
    {
        var tenantId = await SeedTenantAsync();
        var ownerId = await SeedIdentityUserAsync(tenantId, roleCodes: ["OPERATOR"]);
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var adminToken = await GenerateTokenAsync(host, Guid.NewGuid(), tenantId, ["ADMIN"]);
        var property = await CreatePropertyAsync(client, adminToken, "OHT-4");

        var response = await PostAsync(client, $"/api/v1/properties/{property.Id}/owners", new LinkPropertyOwnerRequest(ownerId), adminToken);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task LinkOwner_an_already_linked_pair_returns_409()
    {
        var tenantId = await SeedTenantAsync();
        var ownerId = await SeedEligibleOwnerAsync(tenantId);
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var adminToken = await GenerateTokenAsync(host, Guid.NewGuid(), tenantId, ["ADMIN"]);
        var property = await CreatePropertyAsync(client, adminToken, "OHT-5");
        await PostAsync(client, $"/api/v1/properties/{property.Id}/owners", new LinkPropertyOwnerRequest(ownerId), adminToken);

        var response = await PostAsync(client, $"/api/v1/properties/{property.Id}/owners", new LinkPropertyOwnerRequest(ownerId), adminToken);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task LinkOwner_cross_tenant_returns_404()
    {
        var tenantA = await SeedTenantAsync();
        var tenantB = await SeedTenantAsync();
        var ownerInTenantB = await SeedEligibleOwnerAsync(tenantB);
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var tokenA = await GenerateTokenAsync(host, Guid.NewGuid(), tenantA, ["ADMIN"]);
        var tokenB = await GenerateTokenAsync(host, Guid.NewGuid(), tenantB, ["ADMIN"]);
        var property = await CreatePropertyAsync(client, tokenA, "OHT-6");

        var response = await PostAsync(client, $"/api/v1/properties/{property.Id}/owners", new LinkPropertyOwnerRequest(ownerInTenantB), tokenB);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ---- Tests: UnlinkOwner -----------------------------------------------

    [Fact]
    public async Task UnlinkOwner_an_existing_link_returns_204_with_no_store()
    {
        var tenantId = await SeedTenantAsync();
        var ownerId = await SeedEligibleOwnerAsync(tenantId);
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var adminToken = await GenerateTokenAsync(host, Guid.NewGuid(), tenantId, ["ADMIN"]);
        var property = await CreatePropertyAsync(client, adminToken, "OHT-7");
        await PostAsync(client, $"/api/v1/properties/{property.Id}/owners", new LinkPropertyOwnerRequest(ownerId), adminToken);

        var response = await DeleteAsync(client, $"/api/v1/properties/{property.Id}/owners/{ownerId}", adminToken);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
    }

    [Fact]
    public async Task UnlinkOwner_for_a_nonexistent_property_returns_404()
    {
        var tenantId = await SeedTenantAsync();
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var adminToken = await GenerateTokenAsync(host, Guid.NewGuid(), tenantId, ["ADMIN"]);

        var response = await DeleteAsync(client, $"/api/v1/properties/{Guid.NewGuid()}/owners/{Guid.NewGuid()}", adminToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task UnlinkOwner_a_link_that_does_not_exist_returns_404()
    {
        var tenantId = await SeedTenantAsync();
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var adminToken = await GenerateTokenAsync(host, Guid.NewGuid(), tenantId, ["ADMIN"]);
        var property = await CreatePropertyAsync(client, adminToken, "OHT-8");

        var response = await DeleteAsync(client, $"/api/v1/properties/{property.Id}/owners/{Guid.NewGuid()}", adminToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task UnlinkOwner_repeated_returns_404_the_second_time()
    {
        var tenantId = await SeedTenantAsync();
        var ownerId = await SeedEligibleOwnerAsync(tenantId);
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var adminToken = await GenerateTokenAsync(host, Guid.NewGuid(), tenantId, ["ADMIN"]);
        var property = await CreatePropertyAsync(client, adminToken, "OHT-9");
        await PostAsync(client, $"/api/v1/properties/{property.Id}/owners", new LinkPropertyOwnerRequest(ownerId), adminToken);
        await DeleteAsync(client, $"/api/v1/properties/{property.Id}/owners/{ownerId}", adminToken);

        var response = await DeleteAsync(client, $"/api/v1/properties/{property.Id}/owners/{ownerId}", adminToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ---- Tests: ListOwners (admin) -----------------------------------------

    [Fact]
    public async Task ListOwners_returns_200_with_the_linked_owner()
    {
        var tenantId = await SeedTenantAsync();
        var ownerId = await SeedEligibleOwnerAsync(tenantId);
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var adminToken = await GenerateTokenAsync(host, Guid.NewGuid(), tenantId, ["ADMIN"]);
        var property = await CreatePropertyAsync(client, adminToken, "OHT-10");
        await PostAsync(client, $"/api/v1/properties/{property.Id}/owners", new LinkPropertyOwnerRequest(ownerId), adminToken);

        var response = await GetAsync(client, $"/api/v1/properties/{property.Id}/owners", adminToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<PagedPropertyOwnerResponse>(JsonWebDefaults);
        body!.Items.Should().ContainSingle(o => o.OwnerUserId == ownerId);
    }

    [Fact]
    public async Task ListOwners_for_a_nonexistent_property_returns_404()
    {
        var tenantId = await SeedTenantAsync();
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var adminToken = await GenerateTokenAsync(host, Guid.NewGuid(), tenantId, ["ADMIN"]);

        var response = await GetAsync(client, $"/api/v1/properties/{Guid.NewGuid()}/owners", adminToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ---- Tests: mine (owner self-service) ----------------------------------

    [Fact]
    public async Task Mine_list_without_a_token_returns_401()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();

        var response = await GetAsync(client, "/api/v1/properties/mine", token: null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Mine_list_with_a_role_lacking_PROPERTIES_READ_OWN_OWNER_returns_403()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), Guid.NewGuid(), ["HOUSEKEEPER"]);

        var response = await GetAsync(client, "/api/v1/properties/mine", token);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Mine_list_returns_only_the_callers_own_properties_of_every_status()
    {
        var tenantId = await SeedTenantAsync();
        var ownerId = await SeedEligibleOwnerAsync(tenantId);
        var otherOwnerId = await SeedEligibleOwnerAsync(tenantId);
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var adminToken = await GenerateTokenAsync(host, Guid.NewGuid(), tenantId, ["ADMIN"]);
        var ownProperty = await CreatePropertyAsync(client, adminToken, "OHT-11A");
        var archived = await CreatePropertyAsync(client, adminToken, "OHT-11B");
        await PostAsync(client, $"/api/v1/properties/{archived.Id}/archive", null, adminToken);
        var othersProperty = await CreatePropertyAsync(client, adminToken, "OHT-11C");
        await PostAsync(client, $"/api/v1/properties/{ownProperty.Id}/owners", new LinkPropertyOwnerRequest(ownerId), adminToken);
        await PostAsync(client, $"/api/v1/properties/{archived.Id}/owners", new LinkPropertyOwnerRequest(ownerId), adminToken);
        await PostAsync(client, $"/api/v1/properties/{othersProperty.Id}/owners", new LinkPropertyOwnerRequest(otherOwnerId), adminToken);
        var ownerToken = await GenerateTokenAsync(host, ownerId, tenantId, [IdentityRoleCodes.PropertyOwner]);

        var response = await GetAsync(client, "/api/v1/properties/mine", ownerToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<PagedPropertyResponse>(JsonWebDefaults);
        body!.Items.Select(p => p.Id).Should().BeEquivalentTo([ownProperty.Id, archived.Id]);
    }

    [Fact]
    public async Task Mine_detail_for_a_property_not_linked_to_the_caller_returns_404()
    {
        var tenantId = await SeedTenantAsync();
        var ownerId = await SeedEligibleOwnerAsync(tenantId);
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var adminToken = await GenerateTokenAsync(host, Guid.NewGuid(), tenantId, ["ADMIN"]);
        var property = await CreatePropertyAsync(client, adminToken, "OHT-12");
        var ownerToken = await GenerateTokenAsync(host, ownerId, tenantId, [IdentityRoleCodes.PropertyOwner]);

        var response = await GetAsync(client, $"/api/v1/properties/mine/{property.Id}", ownerToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Mine_detail_for_a_cross_tenant_property_returns_404()
    {
        var tenantA = await SeedTenantAsync();
        var tenantB = await SeedTenantAsync();
        var ownerInTenantB = await SeedEligibleOwnerAsync(tenantB);
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var tokenA = await GenerateTokenAsync(host, Guid.NewGuid(), tenantA, ["ADMIN"]);
        var property = await CreatePropertyAsync(client, tokenA, "OHT-13");
        var ownerTokenB = await GenerateTokenAsync(host, ownerInTenantB, tenantB, [IdentityRoleCodes.PropertyOwner]);

        var response = await GetAsync(client, $"/api/v1/properties/mine/{property.Id}", ownerTokenB);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Mine_detail_for_an_owned_property_returns_200_with_the_effective_address()
    {
        var tenantId = await SeedTenantAsync();
        var ownerId = await SeedEligibleOwnerAsync(tenantId);
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var adminToken = await GenerateTokenAsync(host, Guid.NewGuid(), tenantId, ["ADMIN"]);
        var property = await CreatePropertyAsync(client, adminToken, "OHT-14");
        await PostAsync(client, $"/api/v1/properties/{property.Id}/owners", new LinkPropertyOwnerRequest(ownerId), adminToken);
        var ownerToken = await GenerateTokenAsync(host, ownerId, tenantId, [IdentityRoleCodes.PropertyOwner]);

        var response = await GetAsync(client, $"/api/v1/properties/mine/{property.Id}", ownerToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<PropertyDetailResponse>(JsonWebDefaults);
        body!.Id.Should().Be(property.Id);
        body.EffectiveAddress.Should().NotBeNull();
    }

    // ---- Tests: role loss / link survival ------------------------------------

    /// <summary>
    /// Checkpoint 5 plan, item 17: removing the <c>PROPERTY_OWNER</c> role
    /// blocks NEW access (a fresh token issued without the role — this
    /// codebase authorizes purely from the JWT's own <c>role</c> claims, see
    /// <c>PermissionAuthorizationHandler</c>, never a live per-request DB
    /// check), while the link itself remains stored and visible to an
    /// administrator — never silently removed as a side effect of role loss.
    /// </summary>
    [Fact]
    public async Task Losing_the_PROPERTY_OWNER_role_blocks_mine_access_but_the_link_itself_survives()
    {
        var tenantId = await SeedTenantAsync();
        var ownerId = await SeedEligibleOwnerAsync(tenantId);
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var adminToken = await GenerateTokenAsync(host, Guid.NewGuid(), tenantId, ["ADMIN"]);
        var property = await CreatePropertyAsync(client, adminToken, "OHT-15");
        await PostAsync(client, $"/api/v1/properties/{property.Id}/owners", new LinkPropertyOwnerRequest(ownerId), adminToken);
        // Simulates re-authentication after the role was removed: a fresh
        // token issued with no PROPERTY_OWNER claim at all.
        var tokenWithoutRole = await GenerateTokenAsync(host, ownerId, tenantId, []);

        var mineResponse = await GetAsync(client, "/api/v1/properties/mine", tokenWithoutRole);

        mineResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var adminListResponse = await GetAsync(client, $"/api/v1/properties/{property.Id}/owners", adminToken);
        var adminListBody = await adminListResponse.Content.ReadFromJsonAsync<PagedPropertyOwnerResponse>(JsonWebDefaults);
        adminListBody!.Items.Should().ContainSingle(o => o.OwnerUserId == ownerId);
    }

    [Fact]
    public async Task Restoring_the_PROPERTY_OWNER_role_recovers_mine_access_to_the_surviving_link()
    {
        var tenantId = await SeedTenantAsync();
        var ownerId = await SeedEligibleOwnerAsync(tenantId);
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var adminToken = await GenerateTokenAsync(host, Guid.NewGuid(), tenantId, ["ADMIN"]);
        var property = await CreatePropertyAsync(client, adminToken, "OHT-16");
        await PostAsync(client, $"/api/v1/properties/{property.Id}/owners", new LinkPropertyOwnerRequest(ownerId), adminToken);
        var tokenWithoutRole = await GenerateTokenAsync(host, ownerId, tenantId, []);
        (await GetAsync(client, "/api/v1/properties/mine", tokenWithoutRole)).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // Simulates re-authentication after the role was restored.
        var tokenWithRoleRestored = await GenerateTokenAsync(host, ownerId, tenantId, [IdentityRoleCodes.PropertyOwner]);
        var response = await GetAsync(client, "/api/v1/properties/mine", tokenWithRoleRestored);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<PagedPropertyResponse>(JsonWebDefaults);
        body!.Items.Should().ContainSingle(p => p.Id == property.Id);
    }
}
