using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
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
using IHostPro.Contexts.PropertyManagement.Domain;
using IHostPro.Contexts.PropertyManagement.Domain.ValueObjects;
using IHostPro.Contexts.PropertyManagement.Infrastructure;
using IHostPro.Contexts.PropertyManagement.Infrastructure.Persistence;
using IHostPro.Contexts.Reservations.Api.Contracts;
using IHostPro.Contexts.Reservations.Api.Controllers;
using IHostPro.Contexts.Reservations.Application.Reservations;
using IHostPro.Contexts.Reservations.Domain.Enums;
using IHostPro.Contexts.Reservations.Infrastructure;
using IHostPro.Contexts.Reservations.Infrastructure.Persistence;
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

namespace IHostPro.Contexts.Reservations.Tests.Integration;

/// <summary>
/// End-to-end test of <see cref="ReservationsController"/> against the REAL
/// composition root wiring: real ASP.NET Core host/TestServer, real JWT
/// (issued by Identity's own real stack), real PostgreSQL for Identity
/// (permission catalog), Property Management (property seeding/eligibility)
/// and Reservations. Mirrors <c>CondominiumsEndpointsTests</c>'s structure
/// exactly (Fase 3 §4 — closing the HTTP-level test gaps identified in the
/// continuity audit: 401/403, offset-mandatory/UTC-normalization/invalid-
/// interval at the wire level, period-intersection listing, real HTTP PATCH
/// including the concurrency-conflict path, and PII absence in the three
/// events' actual serialized outbox payload).
/// </summary>
public class ReservationsEndpointsTests : IClassFixture<ReservationsEndpointsTests.Fixture>
{
    private const string IdentityOutboxSchema = "identity_messaging";
    private const string PropertyManagementOutboxSchema = "property_management_messaging";
    private const string ReservationsOutboxSchema = "reservations_messaging";
    private const string MainSchema = "platform_messaging";
    private const string Issuer = "https://identity.ihostpro.test";
    private const string Audience = "ihostpro-api-test";

    private static readonly JsonSerializerOptions JsonWebDefaults = new(JsonSerializerDefaults.Web);

    private readonly string _migratorConnectionString;
    private readonly string _appConnectionString;
    private readonly string _signingKeyPem;

    public ReservationsEndpointsTests(Fixture fixture)
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
            await using (var pmDbContext = CreatePropertyManagementDbContext(MigratorConnectionString))
                await pmDbContext.Database.MigrateAsync();
            await using (var reservationsDbContext = CreateReservationsDbContext(MigratorConnectionString))
                await reservationsDbContext.Database.MigrateAsync();

            await ProvisionMainStoreAsMigratorAsync();
            await ProvisionOutboxAsMigratorAsync(IdentityOutboxSchema, typeof(IdentityDbContext));
            await ProvisionOutboxAsMigratorAsync(PropertyManagementOutboxSchema, typeof(PropertyManagementDbContext));
            await ProvisionOutboxAsMigratorAsync(ReservationsOutboxSchema, typeof(ReservationsDbContext));
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

        private static ReservationsDbContext CreateReservationsDbContext(string connectionString)
        {
            var options = new DbContextOptionsBuilder<ReservationsDbContext>()
                .UseNpgsql(connectionString, npgsqlOptions => npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "reservations"))
                .Options;
            return new ReservationsDbContext(options, new TenantContext());
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

    private async Task<IHost> BuildHostAsync(Action<IServiceCollection>? overrides = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Identity"] = _appConnectionString,
            ["ConnectionStrings:PropertyManagement"] = _appConnectionString,
            ["ConnectionStrings:Reservations"] = _appConnectionString,
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
                    services.AddControllers().AddApplicationPart(typeof(ReservationsController).Assembly);
                    services.AddScoped<ITenantContext, TenantContext>();
                    services.AddScoped<ICurrentTenantProvider, TenantContextCurrentTenantProvider>();
                    services.AddIHostProTenantAwarePipeline();

                    services.AddIdentityModule(configuration, isDevelopmentEnvironment: false);
                    services.AddIdentityJwtIssuance(configuration);
                    services.AddIdentityJwtBearerAuthentication();
                    services.AddIdentityAuthorization();

                    // Property Management's module ONLY — never AddPropertyManagementCommandDispatch:
                    // this host needs only IPropertyReservationEligibilityReader,
                    // never to dispatch a Property Management command.
                    services.AddPropertyManagementModule(configuration, isDevelopmentEnvironment: false);

                    services.AddReservationsModule(configuration);
                    services.AddReservationsCommandDispatch();

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
                opts.PersistMessagesWithPostgresql(_appConnectionString, MainSchema);
                opts.EnrollAncillaryPostgresqlOutbox(_appConnectionString, PropertyManagementOutboxSchema, typeof(PropertyManagementDbContext));
                opts.EnrollAncillaryPostgresqlOutbox(_appConnectionString, ReservationsOutboxSchema, typeof(ReservationsDbContext));
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

    private async Task<Guid> SeedActivePropertyAsync(Guid tenantId, int capacity = 4)
    {
        var address = Address.Create("59090-000", "Rua Exemplo", "100", null, "Ponta Negra", "Natal", "RN", "BR");
        var property = Property.Create(
            Guid.NewGuid(), tenantId, PropertyCode.Create($"P-{Guid.NewGuid():N}"[..12]), "Test Property", capacity,
            condominiumId: null, address, DateTimeOffset.UtcNow);
        property.Activate(DateTimeOffset.UtcNow);

        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);

        await using var dbContext = CreatePropertyManagementDbContext(_migratorConnectionString, tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        dbContext.Properties.Add(property);
        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();

        return property.Id;
    }

    private async Task<Guid> SeedInactivePropertyAsync(Guid tenantId)
    {
        var address = Address.Create("59090-000", "Rua Exemplo", "100", null, "Ponta Negra", "Natal", "RN", "BR");
        var property = Property.Create(
            Guid.NewGuid(), tenantId, PropertyCode.Create($"P-{Guid.NewGuid():N}"[..12]), "Inactive Property", 4,
            condominiumId: null, address, DateTimeOffset.UtcNow);
        // Never Activate()d — stays Draft, which IPropertyReservationEligibilityReader
        // reports as IsActive = false, same as an explicitly Deactivated property.

        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);

        await using var dbContext = CreatePropertyManagementDbContext(_migratorConnectionString, tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        dbContext.Properties.Add(property);
        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();

        return property.Id;
    }

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

    private static async Task<HttpResponseMessage> SendRawJsonAsync(HttpClient client, HttpMethod method, string route, string rawJson, string? token)
    {
        var request = new HttpRequestMessage(method, route) { Content = new StringContent(rawJson, Encoding.UTF8, "application/json") };
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return await client.SendAsync(request);
    }

    // ---- Authentication/authorization (Documento 09 §15) -----------------

    [Fact]
    public async Task Create_without_a_token_returns_401()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();

        var response = await PostAsync(client, "/api/v1/reservations", new CreateReservationRequest(Guid.NewGuid(), "Guest", null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1), 1), token: null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Create_with_a_role_lacking_RESERVATIONS_MANAGE_returns_403()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), Guid.NewGuid(), ["HOUSEKEEPER"]);

        var response = await PostAsync(client, "/api/v1/reservations", new CreateReservationRequest(Guid.NewGuid(), "Guest", null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1), 1), token);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData("ADMIN")]
    [InlineData("OPERATOR")]
    public async Task Create_as_ADMIN_or_OPERATOR_succeeds(string role)
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var tenantId = Guid.NewGuid();
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), tenantId, [role]);
        var propertyId = await SeedActivePropertyAsync(tenantId);

        var response = await PostAsync(
            client, "/api/v1/reservations",
            new CreateReservationRequest(propertyId, "Test Guest", "+5584999999999", new DateTimeOffset(2026, 9, 1, 14, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 9, 3, 11, 0, 0, TimeSpan.Zero), 2),
            token);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    // ---- Create: property eligibility ----------------------------------

    [Fact]
    public async Task Create_for_a_nonexistent_property_returns_404()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var tenantId = Guid.NewGuid();
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), tenantId, ["ADMIN"]);

        var response = await PostAsync(
            client, "/api/v1/reservations",
            new CreateReservationRequest(Guid.NewGuid(), "Test Guest", null, new DateTimeOffset(2026, 9, 1, 14, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 9, 3, 11, 0, 0, TimeSpan.Zero), 2),
            token);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Create_for_an_inactive_property_returns_400_with_PropertyNotActive()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var tenantId = Guid.NewGuid();
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), tenantId, ["ADMIN"]);
        var propertyId = await SeedInactivePropertyAsync(tenantId);

        var response = await PostAsync(
            client, "/api/v1/reservations",
            new CreateReservationRequest(propertyId, "Test Guest", null, new DateTimeOffset(2026, 9, 1, 14, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 9, 3, 11, 0, 0, TimeSpan.Zero), 2),
            token);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Create_exceeding_the_propertys_capacity_returns_400_with_PropertyCapacityExceeded()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var tenantId = Guid.NewGuid();
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), tenantId, ["ADMIN"]);
        var propertyId = await SeedActivePropertyAsync(tenantId, capacity: 1);

        var response = await PostAsync(
            client, "/api/v1/reservations",
            new CreateReservationRequest(propertyId, "Test Guest", null, new DateTimeOffset(2026, 9, 1, 14, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 9, 3, 11, 0, 0, TimeSpan.Zero), 2),
            token);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task An_overlapping_reservation_for_the_same_property_returns_409_with_ReservationDateConflict()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var tenantId = Guid.NewGuid();
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), tenantId, ["ADMIN"]);
        var propertyId = await SeedActivePropertyAsync(tenantId);
        var checkIn = new DateTimeOffset(2026, 9, 1, 14, 0, 0, TimeSpan.Zero);
        var checkOut = new DateTimeOffset(2026, 9, 3, 11, 0, 0, TimeSpan.Zero);
        await PostAsync(client, "/api/v1/reservations", new CreateReservationRequest(propertyId, "Guest A", null, checkIn, checkOut, 1), token);

        var response = await PostAsync(
            client, "/api/v1/reservations",
            new CreateReservationRequest(propertyId, "Guest B", null, checkIn.AddDays(1), checkOut.AddDays(1), 1),
            token);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    // ---- Detail / tenant isolation ---------------------------------------

    [Fact]
    public async Task GetById_for_an_existing_reservation_returns_200()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var tenantId = Guid.NewGuid();
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), tenantId, ["ADMIN"]);
        var propertyId = await SeedActivePropertyAsync(tenantId);
        var created = await PostAsync(client, "/api/v1/reservations", new CreateReservationRequest(propertyId, "Guest", null, new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 9, 3, 0, 0, 0, TimeSpan.Zero), 1), token);
        var createdBody = await created.Content.ReadFromJsonAsync<ReservationDetailResponse>(JsonWebDefaults);

        var response = await GetAsync(client, $"/api/v1/reservations/{createdBody!.Id}", token);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ReservationDetailResponse>(JsonWebDefaults);
        body!.Id.Should().Be(createdBody.Id);
    }

    [Fact]
    public async Task GetById_for_a_cross_tenant_reservation_returns_404()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var tokenA = await GenerateTokenAsync(host, Guid.NewGuid(), tenantA, ["ADMIN"]);
        var tokenB = await GenerateTokenAsync(host, Guid.NewGuid(), tenantB, ["ADMIN"]);
        var propertyId = await SeedActivePropertyAsync(tenantA);
        var created = await PostAsync(client, "/api/v1/reservations", new CreateReservationRequest(propertyId, "Guest", null, new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 9, 3, 0, 0, 0, TimeSpan.Zero), 1), tokenA);
        var createdBody = await created.Content.ReadFromJsonAsync<ReservationDetailResponse>(JsonWebDefaults);

        var response = await GetAsync(client, $"/api/v1/reservations/{createdBody!.Id}", tokenB);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ---- Auditoria (HTTP-triggered, real PostgreSQL) -----------------------

    [Fact]
    public async Task Creating_a_reservation_via_real_http_writes_exactly_one_reservation_created_audit_entry()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var tenantId = Guid.NewGuid();
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), tenantId, ["ADMIN"]);
        var propertyId = await SeedActivePropertyAsync(tenantId);

        var response = await PostAsync(client, "/api/v1/reservations", new CreateReservationRequest(propertyId, "Guest", null, new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 9, 3, 0, 0, 0, TimeSpan.Zero), 1), token);
        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var auditCount = await CountAuditEntriesAsync(tenantId, "reservation_created");
        auditCount.Should().Be(1);
    }

    private async Task<long> CountAuditEntriesAsync(Guid tenantId, string actionCode)
    {
        await using var connection = new NpgsqlConnection(_migratorConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var setCommand = connection.CreateCommand())
        {
            setCommand.CommandText = $"SET LOCAL app.tenant_id = '{tenantId:D}'";
            await setCommand.ExecuteNonQueryAsync();
        }

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM reservations.reservation_audit_log WHERE action_code = @actionCode";
        command.Parameters.AddWithValue("actionCode", actionCode);
        var count = (long)(await command.ExecuteScalarAsync())!;
        await transaction.CommitAsync();
        return count;
    }

    // ---- Date contract (offset mandatory / UTC normalization / invalid interval) ----

    [Fact]
    public async Task Create_with_checkInAt_missing_an_explicit_offset_returns_400()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var tenantId = Guid.NewGuid();
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), tenantId, ["ADMIN"]);
        var propertyId = await SeedActivePropertyAsync(tenantId);

        // "checkInAt" deliberately has no timezone designator ('Z' or
        // +/-HH:mm) — RequireExplicitOffsetDateTimeOffsetConverter must
        // reject this outright, before any handler runs.
        var rawJson = $$"""
            {"propertyId":"{{propertyId}}","guestName":"Test Guest","guestPhone":null,"checkInAt":"2026-09-01T14:00:00","checkOutAt":"2026-09-03T11:00:00-03:00","guestCount":2}
            """;

        var response = await SendRawJsonAsync(client, HttpMethod.Post, "/api/v1/reservations", rawJson, token);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Create_with_checkOutAt_missing_an_explicit_offset_returns_400()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var tenantId = Guid.NewGuid();
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), tenantId, ["ADMIN"]);
        var propertyId = await SeedActivePropertyAsync(tenantId);

        var rawJson = $$"""
            {"propertyId":"{{propertyId}}","guestName":"Test Guest","guestPhone":null,"checkInAt":"2026-09-01T14:00:00-03:00","checkOutAt":"2026-09-03T11:00:00","guestCount":2}
            """;

        var response = await SendRawJsonAsync(client, HttpMethod.Post, "/api/v1/reservations", rawJson, token);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Create_with_a_valid_non_UTC_offset_normalizes_check_in_and_check_out_to_UTC_in_the_response()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var tenantId = Guid.NewGuid();
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), tenantId, ["ADMIN"]);
        var propertyId = await SeedActivePropertyAsync(tenantId);

        var response = await PostAsync(
            client, "/api/v1/reservations",
            new CreateReservationRequest(propertyId, "Test Guest", null, new DateTimeOffset(2026, 9, 1, 14, 0, 0, TimeSpan.FromHours(-3)), new DateTimeOffset(2026, 9, 3, 11, 0, 0, TimeSpan.FromHours(-3)), 2),
            token);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<ReservationDetailResponse>(JsonWebDefaults);
        body!.CheckInAt.Offset.Should().Be(TimeSpan.Zero);
        body.CheckOutAt.Offset.Should().Be(TimeSpan.Zero);
        body.CheckInAt.Should().Be(new DateTimeOffset(2026, 9, 1, 17, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task Create_with_checkOutAt_not_after_checkInAt_returns_400()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var tenantId = Guid.NewGuid();
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), tenantId, ["ADMIN"]);
        var propertyId = await SeedActivePropertyAsync(tenantId);

        var response = await PostAsync(
            client, "/api/v1/reservations",
            new CreateReservationRequest(propertyId, "Test Guest", null, new DateTimeOffset(2026, 9, 3, 11, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 9, 1, 14, 0, 0, TimeSpan.Zero), 2),
            token);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ---- List: period intersection / deterministic ordering ---------------

    [Fact]
    public async Task List_with_from_and_to_returns_only_reservations_whose_period_intersects_the_range()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var tenantId = Guid.NewGuid();
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), tenantId, ["ADMIN"]);
        var propertyId = await SeedActivePropertyAsync(tenantId);

        // Reservation A: Sep 1-3 (before the query window) — must NOT be returned.
        await PostAsync(client, "/api/v1/reservations", new CreateReservationRequest(propertyId, "A", null, new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 9, 3, 0, 0, 0, TimeSpan.Zero), 1), token);
        // Reservation B: Sep 9-11 (overlaps the query window [Sep 10, Sep 20)) — must be returned.
        var b = await PostAsync(client, "/api/v1/reservations", new CreateReservationRequest(propertyId, "B", null, new DateTimeOffset(2026, 9, 9, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 9, 11, 0, 0, 0, TimeSpan.Zero), 1), token);
        // Reservation C: Sep 25-27 (after the query window) — must NOT be returned.
        await PostAsync(client, "/api/v1/reservations", new CreateReservationRequest(propertyId, "C", null, new DateTimeOffset(2026, 9, 25, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 9, 27, 0, 0, 0, TimeSpan.Zero), 1), token);
        var bBody = await b.Content.ReadFromJsonAsync<ReservationDetailResponse>(JsonWebDefaults);

        var from = Uri.EscapeDataString(new DateTimeOffset(2026, 9, 10, 0, 0, 0, TimeSpan.Zero).ToString("O"));
        var to = Uri.EscapeDataString(new DateTimeOffset(2026, 9, 20, 0, 0, 0, TimeSpan.Zero).ToString("O"));
        var response = await GetAsync(client, $"/api/v1/reservations?propertyId={propertyId}&from={from}&to={to}", token);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<PagedReservationResponse>(JsonWebDefaults);
        body!.Items.Should().ContainSingle(i => i.Id == bBody!.Id);
    }

    [Fact]
    public async Task List_orders_deterministically_by_check_in_then_id()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var tenantId = Guid.NewGuid();
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), tenantId, ["ADMIN"]);
        var propertyA = await SeedActivePropertyAsync(tenantId);
        var propertyB = await SeedActivePropertyAsync(tenantId);

        // Same check-in instant on two DIFFERENT properties — the tie must
        // still resolve deterministically, by Id, never by insertion order.
        var sameCheckIn = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        await PostAsync(client, "/api/v1/reservations", new CreateReservationRequest(propertyB, "Later Check-in", null, sameCheckIn.AddDays(5), sameCheckIn.AddDays(7), 1), token);
        await PostAsync(client, "/api/v1/reservations", new CreateReservationRequest(propertyA, "Same Check-in 1", null, sameCheckIn, sameCheckIn.AddDays(1), 1), token);
        await PostAsync(client, "/api/v1/reservations", new CreateReservationRequest(propertyB, "Same Check-in 2", null, sameCheckIn, sameCheckIn.AddDays(1), 1), token);

        var response = await GetAsync(client, "/api/v1/reservations?pageSize=10", token);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<PagedReservationResponse>(JsonWebDefaults);
        body!.Items.Should().BeInAscendingOrder(i => i.CheckInAt)
            .And.HaveCount(3);
        // Within the tied check-in group, ascending by Id.
        var tied = body.Items.Where(i => i.CheckInAt == sameCheckIn).ToArray();
        tied.Should().BeInAscendingOrder(i => i.Id);
    }

    // ---- PATCH: real HTTP guestPhone presence-awareness --------------------

    [Fact]
    public async Task Update_with_guestPhone_explicit_null_removes_it_via_real_http()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var tenantId = Guid.NewGuid();
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), tenantId, ["ADMIN"]);
        var propertyId = await SeedActivePropertyAsync(tenantId);
        var created = await PostAsync(client, "/api/v1/reservations", new CreateReservationRequest(propertyId, "Guest", "+5584999999999", new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 9, 3, 0, 0, 0, TimeSpan.Zero), 1), token);
        var createdBody = await created.Content.ReadFromJsonAsync<ReservationDetailResponse>(JsonWebDefaults);

        var rawJson = """{"guestPhone":null}""";
        var response = await SendRawJsonAsync(client, HttpMethod.Patch, $"/api/v1/reservations/{createdBody!.Id}", rawJson, token);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ReservationDetailResponse>(JsonWebDefaults);
        body!.GuestPhone.Should().BeNull();
    }

    [Fact]
    public async Task Update_with_guestPhone_omitted_keeps_it_unchanged_via_real_http()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var tenantId = Guid.NewGuid();
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), tenantId, ["ADMIN"]);
        var propertyId = await SeedActivePropertyAsync(tenantId);
        var created = await PostAsync(client, "/api/v1/reservations", new CreateReservationRequest(propertyId, "Guest", "+5584999999999", new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 9, 3, 0, 0, 0, TimeSpan.Zero), 1), token);
        var createdBody = await created.Content.ReadFromJsonAsync<ReservationDetailResponse>(JsonWebDefaults);

        // guestPhone entirely absent from the JSON body — only guestName supplied.
        var rawJson = """{"guestName":"New Name"}""";
        var response = await SendRawJsonAsync(client, HttpMethod.Patch, $"/api/v1/reservations/{createdBody!.Id}", rawJson, token);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ReservationDetailResponse>(JsonWebDefaults);
        body!.GuestPhone.Should().Be("+5584999999999");
    }

    [Fact]
    public async Task Update_with_checkInAt_missing_an_explicit_offset_returns_400()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var tenantId = Guid.NewGuid();
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), tenantId, ["ADMIN"]);
        var propertyId = await SeedActivePropertyAsync(tenantId);
        var created = await PostAsync(client, "/api/v1/reservations", new CreateReservationRequest(propertyId, "Guest", null, new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 9, 3, 0, 0, 0, TimeSpan.Zero), 1), token);
        var createdBody = await created.Content.ReadFromJsonAsync<ReservationDetailResponse>(JsonWebDefaults);

        // "checkInAt" deliberately has no timezone designator —
        // OptionalJsonConverter<T>'s DateTimeOffset special case
        // (RequireExplicitOffsetDateTimeOffsetConverter) must reject this.
        var rawJson = """{"checkInAt":"2026-09-05T14:00:00"}""";
        var response = await SendRawJsonAsync(client, HttpMethod.Patch, $"/api/v1/reservations/{createdBody!.Id}", rawJson, token);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ---- PATCH: concurrency conflict via real HTTP -------------------------

    /// <summary>
    /// Test-only decorator — wraps the real <see cref="ReservationReader"/>,
    /// signals <paramref name="snapshotCaptured"/> right after the
    /// pre-transaction snapshot (Fase 3 §3 steps 1-3) is read, then blocks
    /// until <paramref name="releaseGate"/> completes — giving the test a
    /// deterministic window to mutate the SAME row through a genuinely
    /// separate connection before the write transaction's own re-read.
    /// Mirrors <c>ReservationCommandHandlerTests.GatedSnapshotReservationReader</c>
    /// exactly, redeclared here since test-only decorators are not shared
    /// across test files in this codebase. Registered ONLY in this test
    /// host's DI container — no test-only hook exists in production code.
    /// </summary>
    private sealed class GatedSnapshotReservationReader : IReservationReader
    {
        private readonly IReservationReader _inner;
        private readonly TaskCompletionSource _snapshotCaptured;
        private readonly TaskCompletionSource _releaseGate;

        public GatedSnapshotReservationReader(IReservationReader inner, TaskCompletionSource snapshotCaptured, TaskCompletionSource releaseGate)
        {
            _inner = inner;
            _snapshotCaptured = snapshotCaptured;
            _releaseGate = releaseGate;
        }

        public Task<PagedResult<ReservationSummaryResult>> ListAsync(
            Guid? propertyId, string? status, DateTimeOffset? from, DateTimeOffset? to,
            int page, int pageSize, CancellationToken cancellationToken) =>
            _inner.ListAsync(propertyId, status, from, to, page, pageSize, cancellationToken);

        public Task<ReservationResult?> GetByIdAsync(Guid reservationId, CancellationToken cancellationToken) =>
            _inner.GetByIdAsync(reservationId, cancellationToken);

        public Task<uint?> GetCurrentXminAsync(Guid reservationId, CancellationToken cancellationToken) =>
            _inner.GetCurrentXminAsync(reservationId, cancellationToken);

        public Task<Guid?> GetIdByExternalIdentityAsync(
            ReservationSource source, string externalReservationId, CancellationToken cancellationToken) =>
            _inner.GetIdByExternalIdentityAsync(source, externalReservationId, cancellationToken);

        public async Task<ReservationUpdateSnapshot?> GetUpdateSnapshotAsync(Guid reservationId, CancellationToken cancellationToken)
        {
            var snapshot = await _inner.GetUpdateSnapshotAsync(reservationId, cancellationToken);
            _snapshotCaptured.TrySetResult();
            await _releaseGate.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            return snapshot;
        }
    }

    [Fact]
    public async Task Update_returns_409_when_the_row_changed_between_the_snapshot_and_the_write_transaction()
    {
        var tenantId = Guid.NewGuid();
        var snapshotCaptured = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var host = await BuildHostAsync(services =>
            services.AddScoped<IReservationReader>(sp =>
                new GatedSnapshotReservationReader(
                    new ReservationReader(sp.GetRequiredService<ReservationsDbContext>(), sp.GetRequiredService<ITenantContext>()),
                    snapshotCaptured, releaseGate)));
        using var client = host.GetTestClient();
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), tenantId, ["ADMIN"]);
        var propertyId = await SeedActivePropertyAsync(tenantId);
        var created = await PostAsync(client, "/api/v1/reservations", new CreateReservationRequest(propertyId, "Guest", null, new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 9, 3, 0, 0, 0, TimeSpan.Zero), 1), token);
        var createdBody = await created.Content.ReadFromJsonAsync<ReservationDetailResponse>(JsonWebDefaults);

        var updateTask = PatchAsync(client, $"/api/v1/reservations/{createdBody!.Id}", new { guestName = "New Name" }, token);

        var snapshotReady = await Task.WhenAny(snapshotCaptured.Task, Task.Delay(TimeSpan.FromSeconds(10))) == snapshotCaptured.Task;
        snapshotReady.Should().BeTrue("the update must reach the pre-transaction snapshot read within 10s");

        await using (var connection = new NpgsqlConnection(_migratorConnectionString))
        {
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await using (var setCommand = connection.CreateCommand())
            {
                setCommand.CommandText = $"SET LOCAL app.tenant_id = '{tenantId:D}'";
                await setCommand.ExecuteNonQueryAsync();
            }
            await using (var mutateCommand = connection.CreateCommand())
            {
                mutateCommand.CommandText = "UPDATE reservations.reservations SET guest_count = guest_count WHERE id = @id";
                mutateCommand.Parameters.AddWithValue("id", createdBody.Id);
                await mutateCommand.ExecuteNonQueryAsync();
            }
            await transaction.CommitAsync();
        }

        releaseGate.TrySetResult();

        var response = await updateTask;
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    // ---- Cancel -------------------------------------------------------------

    [Fact]
    public async Task Cancelling_twice_returns_409_the_second_time()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var tenantId = Guid.NewGuid();
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), tenantId, ["ADMIN"]);
        var propertyId = await SeedActivePropertyAsync(tenantId);
        var created = await PostAsync(client, "/api/v1/reservations", new CreateReservationRequest(propertyId, "Guest", null, new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 9, 3, 0, 0, 0, TimeSpan.Zero), 1), token);
        var createdBody = await created.Content.ReadFromJsonAsync<ReservationDetailResponse>(JsonWebDefaults);

        var first = await PostAsync(client, $"/api/v1/reservations/{createdBody!.Id}/cancel", null, token);
        var second = await PostAsync(client, $"/api/v1/reservations/{createdBody.Id}/cancel", null, token);

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    // ---- Helpers -----------------------------------------------------------

    private static async Task SetTenantAsync(PropertyManagementDbContext dbContext, Guid tenantId) =>
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.tenant_id', {tenantId.ToString()}, true)");

    private static PropertyManagementDbContext CreatePropertyManagementDbContext(string connectionString, ITenantContext? tenantContext = null)
    {
        var options = new DbContextOptionsBuilder<PropertyManagementDbContext>()
            .UseNpgsql(connectionString, npgsqlOptions => npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "property_management"))
            .Options;

        return new PropertyManagementDbContext(options, tenantContext ?? new TenantContext());
    }
}
