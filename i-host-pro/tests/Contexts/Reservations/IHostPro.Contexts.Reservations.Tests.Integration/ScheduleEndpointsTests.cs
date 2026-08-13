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
using IHostPro.Contexts.Reservations.Api.Contracts;
using IHostPro.Contexts.Reservations.Api.Controllers;
using IHostPro.Contexts.Reservations.Domain;
using IHostPro.Contexts.Reservations.Infrastructure;
using IHostPro.Contexts.Reservations.Infrastructure.Persistence;
using IHostPro.Contexts.Reservations.Infrastructure.Projections;
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
/// End-to-end test of <see cref="ScheduleController"/> against the REAL
/// composition root wiring (real ASP.NET Core host/TestServer, real JWT,
/// real PostgreSQL for Identity's permission catalog and Reservations'
/// <c>reservations</c>/<c>reservation_audit_log</c>/<c>cleaning_schedule_projection</c>
/// tables) — Fase 7, Incremento 1 (Agenda Foundation, Checkpoint 1). Mirrors
/// <c>ReservationsEndpointsTests</c>'s structure, but omits Property
/// Management entirely — <c>GET /api/v1/schedule</c> is read-only and never
/// consults <c>IPropertyReservationEligibilityReader</c>. Cleaning items are
/// seeded directly into <see cref="CleaningScheduleProjectionEntry"/> — the
/// real event-consumption path (CleaningCreated → real Worker → this same
/// table) is already proven separately by
/// <c>CleaningCreatedScheduleProjectionWorkerRoundTripTests</c> (Checkpoint
/// 1, item F); this file's own subject is the read/composition/authorization
/// API layer built on top of that table, not event delivery again.
/// </summary>
public class ScheduleEndpointsTests : IClassFixture<ScheduleEndpointsTests.Fixture>
{
    private const string IdentityOutboxSchema = "identity_messaging";
    private const string ReservationsOutboxSchema = "reservations_messaging";
    private const string MainSchema = "platform_messaging";
    private const string Issuer = "https://identity.ihostpro.test";
    private const string Audience = "ihostpro-api-test";

    private static readonly JsonSerializerOptions JsonWebDefaults = new(JsonSerializerDefaults.Web);

    private readonly string _migratorConnectionString;
    private readonly string _appConnectionString;
    private readonly string _signingKeyPem;

    public ScheduleEndpointsTests(Fixture fixture)
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
            await using (var reservationsDbContext = CreateReservationsDbContext(MigratorConnectionString))
                await reservationsDbContext.Database.MigrateAsync();

            await ProvisionMainStoreAsMigratorAsync();
            await ProvisionOutboxAsMigratorAsync(IdentityOutboxSchema, typeof(IdentityDbContext));
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

    private async Task<IHost> BuildHostAsync()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Identity"] = _appConnectionString,
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
                    services.AddControllers().AddApplicationPart(typeof(ScheduleController).Assembly);
                    services.AddScoped<ITenantContext, TenantContext>();
                    services.AddScoped<ICurrentTenantProvider, TenantContextCurrentTenantProvider>();
                    services.AddIHostProTenantAwarePipeline();

                    services.AddIdentityModule(configuration, isDevelopmentEnvironment: false);
                    services.AddIdentityJwtIssuance(configuration);
                    services.AddIdentityJwtBearerAuthentication();
                    services.AddIdentityAuthorization();

                    services.AddReservationsModule(configuration);
                    services.AddReservationsCommandDispatch();
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

    // ---- Seeding ------------------------------------------------------------

    private async Task<Guid> SeedReservationAsync(Guid tenantId, Guid propertyId, DateTimeOffset checkInAt, DateTimeOffset checkOutAt)
    {
        var now = DateTimeOffset.UtcNow;
        var reservation = Reservation.Create(
            Guid.NewGuid(), tenantId, propertyId, "Test Guest", null, checkInAt, checkOutAt, guestCount: 2, now);

        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);

        await using var dbContext = CreateReservationsDbContext(_migratorConnectionString, tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        dbContext.Reservations.Add(reservation);
        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();

        return reservation.Id;
    }

    private async Task<Guid> SeedCleaningScheduleEntryAsync(
        Guid tenantId, Guid propertyId, DateTimeOffset scheduledAtUtc, string status = "Pending", Guid? housekeeperUserId = null)
    {
        var cleaningId = Guid.NewGuid();
        var entry = new CleaningScheduleProjectionEntry(tenantId, cleaningId, propertyId, status, scheduledAtUtc);
        if (housekeeperUserId is Guid effectiveHousekeeperUserId)
            entry.SetAssignedHousekeeper(effectiveHousekeeperUserId);

        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);

        await using var dbContext = CreateReservationsDbContext(_migratorConnectionString, tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        dbContext.CleaningScheduleProjection.Add(entry);
        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();

        return cleaningId;
    }

    private static async Task SetTenantAsync(ReservationsDbContext dbContext, Guid tenantId) =>
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.tenant_id', {tenantId.ToString()}, true)");

    private static ReservationsDbContext CreateReservationsDbContext(string connectionString, ITenantContext? tenantContext = null)
    {
        var options = new DbContextOptionsBuilder<ReservationsDbContext>()
            .UseNpgsql(connectionString, npgsqlOptions => npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "reservations"))
            .Options;
        return new ReservationsDbContext(options, tenantContext ?? new TenantContext());
    }

    // ---- HTTP helpers ---------------------------------------------------

    private static async Task<HttpResponseMessage> GetAsync(HttpClient client, string route, string? token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, route);
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return await client.SendAsync(request);
    }

    // ---- Authentication/authorization -----------------------------------

    [Fact]
    public async Task List_without_a_token_returns_401()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();

        var response = await GetAsync(
            client, $"/api/v1/schedule?from={Uri.EscapeDataString(DateTimeOffset.UtcNow.ToString("O"))}&to={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(1).ToString("O"))}",
            token: null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task List_with_a_role_holding_neither_SCHEDULE_MANAGE_nor_SCHEDULE_READ_returns_403()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        // PROPERTY_OWNER only holds SCHEDULE:READ:OWN_OWNER, which this
        // increment's endpoint deliberately never checks (Checkpoint 0 gate)
        // — confirms it is genuinely denied, not accidentally admitted.
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), Guid.NewGuid(), ["PROPERTY_OWNER"]);

        var response = await GetAsync(
            client, $"/api/v1/schedule?from={Uri.EscapeDataString(DateTimeOffset.UtcNow.ToString("O"))}&to={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(1).ToString("O"))}",
            token);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData("ADMIN")]
    [InlineData("OPERATOR")]
    [InlineData("HOUSEKEEPER")]
    public async Task List_with_a_role_holding_either_SCHEDULE_MANAGE_or_SCHEDULE_READ_returns_200(string role)
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var tenantId = Guid.NewGuid();
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), tenantId, [role]);

        var response = await GetAsync(
            client, $"/api/v1/schedule?from={Uri.EscapeDataString(DateTimeOffset.UtcNow.ToString("O"))}&to={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(1).ToString("O"))}",
            token);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ---- Composition ------------------------------------------------------

    [Fact]
    public async Task List_composes_Reservation_and_Cleaning_items_correctly_typed_and_shaped()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var tenantId = Guid.NewGuid();
        var propertyId = Guid.NewGuid();
        var housekeeperUserId = Guid.NewGuid();
        var windowStart = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        var windowEnd = new DateTimeOffset(2026, 9, 30, 0, 0, 0, TimeSpan.Zero);
        var checkInAt = new DateTimeOffset(2026, 9, 10, 14, 0, 0, TimeSpan.Zero);
        var checkOutAt = new DateTimeOffset(2026, 9, 12, 11, 0, 0, TimeSpan.Zero);
        var scheduledAtUtc = new DateTimeOffset(2026, 9, 12, 13, 0, 0, TimeSpan.Zero);

        var reservationId = await SeedReservationAsync(tenantId, propertyId, checkInAt, checkOutAt);
        var cleaningId = await SeedCleaningScheduleEntryAsync(tenantId, propertyId, scheduledAtUtc, "Assigned", housekeeperUserId);

        var token = await GenerateTokenAsync(host, Guid.NewGuid(), tenantId, ["ADMIN"]);
        var response = await GetAsync(
            client,
            $"/api/v1/schedule?from={Uri.EscapeDataString(windowStart.ToString("O"))}&to={Uri.EscapeDataString(windowEnd.ToString("O"))}",
            token);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var items = (await response.Content.ReadFromJsonAsync<ScheduleItemResponse[]>(JsonWebDefaults))!;

        items.Should().HaveCount(2);

        var reservationItem = items.Single(i => i.Type == "Reservation");
        reservationItem.Id.Should().Be(reservationId);
        reservationItem.SourceReferenceId.Should().Be(reservationId);
        reservationItem.PropertyId.Should().Be(propertyId);
        reservationItem.StartAtUtc.Should().Be(checkInAt);
        reservationItem.EndAtUtc.Should().Be(checkOutAt);
        reservationItem.Status.Should().Be("confirmed");
        reservationItem.HousekeeperUserId.Should().BeNull();

        var cleaningItem = items.Single(i => i.Type == "Cleaning");
        cleaningItem.Id.Should().Be(cleaningId);
        cleaningItem.SourceReferenceId.Should().Be(cleaningId);
        cleaningItem.PropertyId.Should().Be(propertyId);
        cleaningItem.StartAtUtc.Should().Be(scheduledAtUtc);
        cleaningItem.EndAtUtc.Should().BeNull();
        cleaningItem.Status.Should().Be("Assigned");
        cleaningItem.HousekeeperUserId.Should().Be(housekeeperUserId);
    }

    [Fact]
    public async Task List_filters_by_propertyId()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var tenantId = Guid.NewGuid();
        var matchingPropertyId = Guid.NewGuid();
        var otherPropertyId = Guid.NewGuid();
        var windowStart = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        var windowEnd = new DateTimeOffset(2026, 10, 31, 0, 0, 0, TimeSpan.Zero);

        await SeedCleaningScheduleEntryAsync(tenantId, matchingPropertyId, new DateTimeOffset(2026, 10, 5, 9, 0, 0, TimeSpan.Zero));
        await SeedCleaningScheduleEntryAsync(tenantId, otherPropertyId, new DateTimeOffset(2026, 10, 6, 9, 0, 0, TimeSpan.Zero));

        var token = await GenerateTokenAsync(host, Guid.NewGuid(), tenantId, ["ADMIN"]);
        var response = await GetAsync(
            client,
            $"/api/v1/schedule?from={Uri.EscapeDataString(windowStart.ToString("O"))}&to={Uri.EscapeDataString(windowEnd.ToString("O"))}&propertyId={matchingPropertyId}",
            token);

        var items = (await response.Content.ReadFromJsonAsync<ScheduleItemResponse[]>(JsonWebDefaults))!;

        items.Should().ContainSingle();
        items[0].PropertyId.Should().Be(matchingPropertyId);
    }

    [Fact]
    public async Task List_never_returns_another_tenants_items()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var ownTenantId = Guid.NewGuid();
        var otherTenantId = Guid.NewGuid();
        var propertyId = Guid.NewGuid();
        var windowStart = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
        var windowEnd = new DateTimeOffset(2026, 11, 30, 0, 0, 0, TimeSpan.Zero);

        await SeedCleaningScheduleEntryAsync(otherTenantId, propertyId, new DateTimeOffset(2026, 11, 5, 9, 0, 0, TimeSpan.Zero));

        var token = await GenerateTokenAsync(host, Guid.NewGuid(), ownTenantId, ["ADMIN"]);
        var response = await GetAsync(
            client,
            $"/api/v1/schedule?from={Uri.EscapeDataString(windowStart.ToString("O"))}&to={Uri.EscapeDataString(windowEnd.ToString("O"))}",
            token);

        var items = (await response.Content.ReadFromJsonAsync<ScheduleItemResponse[]>(JsonWebDefaults))!;

        items.Should().BeEmpty("a different tenant's RLS-scoped connection must never see this tenant's schedule items");
    }

    // ---- Validation --------------------------------------------------------

    [Fact]
    public async Task List_with_to_not_after_from_returns_400()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), Guid.NewGuid(), ["ADMIN"]);
        var moment = DateTimeOffset.UtcNow;

        var response = await GetAsync(
            client, $"/api/v1/schedule?from={Uri.EscapeDataString(moment.ToString("O"))}&to={Uri.EscapeDataString(moment.ToString("O"))}", token);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task List_with_a_window_larger_than_100_days_returns_400()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), Guid.NewGuid(), ["ADMIN"]);
        var from = DateTimeOffset.UtcNow;
        var to = from.AddDays(101);

        var response = await GetAsync(
            client, $"/api/v1/schedule?from={Uri.EscapeDataString(from.ToString("O"))}&to={Uri.EscapeDataString(to.ToString("O"))}", token);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
