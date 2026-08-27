using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using FluentAssertions;
using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Infrastructure.Messaging;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.BuildingBlocks.Infrastructure.Persistence;
using IHostPro.Contexts.Configuration.Infrastructure;
using IHostPro.Contexts.Configuration.Infrastructure.Persistence;
using IHostPro.Contexts.GuestOperations.Api.Controllers;
using IHostPro.Contexts.GuestOperations.Domain;
using IHostPro.Contexts.GuestOperations.Infrastructure;
using IHostPro.Contexts.GuestOperations.Infrastructure.Persistence;
using IHostPro.Contexts.Housekeeping.Infrastructure;
using IHostPro.Contexts.Housekeeping.Infrastructure.Persistence;
using IHostPro.Contexts.Identity.Api.Authorization;
using IHostPro.Contexts.Identity.Application;
using IHostPro.Contexts.Identity.Infrastructure;
using IHostPro.Contexts.Identity.Infrastructure.Authentication;
using IHostPro.Contexts.Identity.Infrastructure.Persistence;
using IHostPro.Contexts.Reservations.Domain;
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

namespace IHostPro.Contexts.GuestOperations.Tests.Integration;

/// <summary>
/// Fase 10, Checkpoint 3 (Early Check-in / Late Checkout) — proves the two
/// new endpoints against the REAL ASP.NET Core HTTP + authorization pipeline
/// (real JWT bearer validation, real <c>GUEST_OPERATIONS:MANAGE</c> policy
/// resolution), mirroring <c>GuestStayOperationsControllerAuthorizationTests</c>'
/// own host-building structure exactly, extended with the three additional
/// modules (<c>Reservations</c>/<c>Housekeeping</c>/<c>Configuration</c>)
/// these two commands' real cross-context readers need — never faked here
/// either (mandate §8). No RabbitMQ transport is configured: nothing here
/// asserts on message delivery (that is
/// <c>EarlyCheckInLateCheckoutWorkflowRoundTripTests</c>' own job, run
/// separately), only on the real HTTP authorization/business outcome.
/// "Success" for the ADMIN+MANAGE case deliberately means a real 200 with a
/// genuinely evaluated (here: policy-not-configured Denied) outcome —
/// exercising Reservations'/Housekeeping's/Configuration's real
/// Infrastructure readers end-to-end, never an in-process fake.
/// </summary>
public class EarlyCheckInLateCheckoutAuthorizationTests : IClassFixture<EarlyCheckInLateCheckoutAuthorizationTests.Fixture>
{
    private const string GuestOperationsOutboxSchema = "guest_operations_messaging";
    private const string MainSchema = "platform_messaging";
    private const string Issuer = "https://identity.ihostpro.test";
    private const string Audience = "ihostpro-api-test";

    private readonly string _migratorConnectionString;
    private readonly string _appConnectionString;
    private readonly string _signingKeyPem;

    public EarlyCheckInLateCheckoutAuthorizationTests(Fixture fixture)
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
            await using (var guestOperationsDbContext = CreateGuestOperationsDbContext(MigratorConnectionString))
                await guestOperationsDbContext.Database.MigrateAsync();
            await using (var reservationsDbContext = CreateReservationsDbContext(MigratorConnectionString))
                await reservationsDbContext.Database.MigrateAsync();
            await using (var housekeepingDbContext = CreateHousekeepingDbContext(MigratorConnectionString))
                await housekeepingDbContext.Database.MigrateAsync();
            await using (var configurationDbContext = CreateConfigurationDbContext(MigratorConnectionString))
                await configurationDbContext.Database.MigrateAsync();

            await ProvisionMainStoreAsMigratorAsync();
            await ProvisionOutboxAsMigratorAsync(GuestOperationsOutboxSchema, typeof(GuestOperationsDbContext));
        }

        public async Task DisposeAsync() => await _container.DisposeAsync();

        private static IdentityDbContext CreateIdentityDbContext(string connectionString)
        {
            var options = new DbContextOptionsBuilder<IdentityDbContext>()
                .UseNpgsql(connectionString, npgsqlOptions => npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "identity"))
                .Options;
            return new IdentityDbContext(options, new TenantContext());
        }

        private static GuestOperationsDbContext CreateGuestOperationsDbContext(string connectionString)
        {
            var options = new DbContextOptionsBuilder<GuestOperationsDbContext>()
                .UseNpgsql(connectionString, npgsqlOptions => npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "guest_operations"))
                .Options;
            return new GuestOperationsDbContext(options, new TenantContext());
        }

        private static ReservationsDbContext CreateReservationsDbContext(string connectionString)
        {
            var options = new DbContextOptionsBuilder<ReservationsDbContext>()
                .UseNpgsql(connectionString, npgsqlOptions => npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "reservations"))
                .Options;
            return new ReservationsDbContext(options, new TenantContext());
        }

        private static HousekeepingDbContext CreateHousekeepingDbContext(string connectionString)
        {
            var options = new DbContextOptionsBuilder<HousekeepingDbContext>()
                .UseNpgsql(connectionString, npgsqlOptions => npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "housekeeping"))
                .Options;
            return new HousekeepingDbContext(options, new TenantContext());
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
            ["ConnectionStrings:GuestOperations"] = _appConnectionString,
            ["ConnectionStrings:Reservations"] = _appConnectionString,
            ["ConnectionStrings:Housekeeping"] = _appConnectionString,
            ["ConnectionStrings:Configuration"] = _appConnectionString,
            ["Configuration:PolicyCache:ConnectionString"] = "localhost:6379",
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
                    services.AddControllers().AddApplicationPart(typeof(GuestStayOperationsController).Assembly);
                    services.AddScoped<ITenantContext, TenantContext>();
                    services.AddScoped<ICurrentTenantProvider, TenantContextCurrentTenantProvider>();
                    services.AddIHostProTenantAwarePipeline();

                    services.AddIdentityModule(configuration, isDevelopmentEnvironment: false);
                    services.AddIdentityJwtIssuance(configuration);
                    services.AddIdentityJwtBearerAuthentication();
                    services.AddIdentityAuthorization();

                    // The three real cross-context modules the two new
                    // commands' readers need — never faked (mandate §8).
                    // Only the base module registration from each — no
                    // command dispatch/outbox for any of them, since nothing
                    // exercised here ever writes through Reservations'/
                    // Housekeeping's/Configuration's own command pipeline.
                    services.AddReservationsModule(configuration);
                    services.AddHousekeepingModule(configuration);
                    services.AddConfigurationModule(configuration);

                    services.AddGuestOperationsModule(configuration);
                    services.AddGuestOperationsCommandDispatch();
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
                opts.EnrollAncillaryPostgresqlOutbox(_appConnectionString, GuestOperationsOutboxSchema, typeof(GuestOperationsDbContext));
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

    private static async Task<HttpResponseMessage> PostJsonAsync(HttpClient client, string route, string? token, object? body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, route);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return await client.SendAsync(request);
    }

    // ---- Seeding ------------------------------------------------------------

    private async Task<Guid> SeedActiveGuestStayOperationAsync(Guid tenantId, Guid reservationId, Guid propertyId)
    {
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);

        var options = new DbContextOptionsBuilder<GuestOperationsDbContext>()
            .UseNpgsql(_appConnectionString, npgsqlOptions => npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "guest_operations"))
            .Options;
        await using var dbContext = new GuestOperationsDbContext(options, tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        var operation = GuestStayOperation.Create(Guid.NewGuid(), tenantId, reservationId, propertyId, DateTimeOffset.UtcNow);
        dbContext.GuestStayOperations.Add(operation);
        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();

        return operation.Id;
    }

    private async Task<Guid> SeedConfirmedReservationAsync(Guid tenantId, DateTimeOffset checkInAt, DateTimeOffset checkOutAt)
    {
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);

        var options = new DbContextOptionsBuilder<ReservationsDbContext>()
            .UseNpgsql(_appConnectionString, npgsqlOptions => npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "reservations"))
            .Options;
        await using var dbContext = new ReservationsDbContext(options, tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        var reservation = Reservation.Create(
            Guid.NewGuid(), tenantId, Guid.NewGuid(), "Test Guest", null, checkInAt, checkOutAt, guestCount: 2, DateTimeOffset.UtcNow);
        dbContext.Reservations.Add(reservation);
        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();

        return reservation.Id;
    }

    private static async Task SetTenantAsync(DbContext dbContext, Guid tenantId) =>
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.tenant_id', {tenantId.ToString()}, true)");

    // ---- Tests: POST early-check-in ----------------------------------------

    [Fact]
    public async Task EarlyCheckIn_without_a_token_returns_401()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();

        var response = await PostJsonAsync(client, $"/api/v1/guest-operations/reservations/{Guid.NewGuid()}/early-check-in", null,
            new { requestedCheckInAt = DateTimeOffset.UtcNow });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task EarlyCheckIn_with_a_role_lacking_GUEST_OPERATIONS_MANAGE_returns_403_never_500()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), Guid.NewGuid(), ["AI_AGENT"]);

        var response = await PostJsonAsync(client, $"/api/v1/guest-operations/reservations/{Guid.NewGuid()}/early-check-in", token,
            new { requestedCheckInAt = DateTimeOffset.UtcNow });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "a role without GUEST_OPERATIONS:MANAGE must be cleanly denied — never a 500 from an unregistered policy");
    }

    [Fact]
    public async Task EarlyCheckIn_as_ADMIN_reaches_the_real_handler_and_returns_a_real_evaluated_200()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var tenantId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var checkInAt = now.AddDays(3);
        var reservationId = await SeedConfirmedReservationAsync(tenantId, checkInAt, now.AddDays(5));
        await SeedActiveGuestStayOperationAsync(tenantId, reservationId, Guid.NewGuid());
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), tenantId, ["ADMIN"]);

        var response = await PostJsonAsync(client, $"/api/v1/guest-operations/reservations/{reservationId}/early-check-in", token,
            new { requestedCheckInAt = checkInAt.AddHours(-2) });

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "an ADMIN caller must pass real policy resolution and reach the real command handler — this proves the real " +
            "IReservationScheduleReader/IEarlyCheckInPolicyReader Infrastructure implementations, never a fake");
        var body = await response.Content.ReadFromJsonAsync<EarlyCheckInResponseShape>();
        body!.Status.Should().Be("denied");
        body.DenialReasonCode.Should().Be("policy_not_configured",
            "no EARLY_CHECKIN policy was seeded for this tenant — NotConfigured must translate to a real Denied outcome, never a 500");
    }

    [Fact]
    public async Task EarlyCheckIn_for_a_different_tenants_reservation_returns_404_never_leaking_existence()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var ownerTenantId = Guid.NewGuid();
        var otherTenantId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var reservationId = await SeedConfirmedReservationAsync(ownerTenantId, now.AddDays(3), now.AddDays(5));
        await SeedActiveGuestStayOperationAsync(ownerTenantId, reservationId, Guid.NewGuid());
        var otherTenantToken = await GenerateTokenAsync(host, Guid.NewGuid(), otherTenantId, ["ADMIN"]);

        var response = await PostJsonAsync(client, $"/api/v1/guest-operations/reservations/{reservationId}/early-check-in", otherTenantToken,
            new { requestedCheckInAt = now.AddDays(3).AddHours(-2) });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "a different tenant's RLS-scoped connection must never see this tenant's GuestStayOperation");
    }

    // ---- Tests: POST late-checkout ------------------------------------------

    [Fact]
    public async Task LateCheckout_without_a_token_returns_401()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();

        var response = await PostJsonAsync(client, $"/api/v1/guest-operations/reservations/{Guid.NewGuid()}/late-checkout", null,
            new { requestedCheckOutAt = DateTimeOffset.UtcNow });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task LateCheckout_with_a_role_lacking_GUEST_OPERATIONS_MANAGE_returns_403_never_500()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), Guid.NewGuid(), ["AI_AGENT"]);

        var response = await PostJsonAsync(client, $"/api/v1/guest-operations/reservations/{Guid.NewGuid()}/late-checkout", token,
            new { requestedCheckOutAt = DateTimeOffset.UtcNow });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task LateCheckout_as_ADMIN_reaches_the_real_handler_and_returns_a_real_evaluated_200()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var tenantId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var checkOutAt = now.AddDays(5);
        var reservationId = await SeedConfirmedReservationAsync(tenantId, now.AddDays(3), checkOutAt);
        var operationId = await SeedActiveGuestStayOperationAsync(tenantId, reservationId, Guid.NewGuid());
        await CheckInOperationAsync(tenantId, operationId);
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), tenantId, ["ADMIN"]);

        var response = await PostJsonAsync(client, $"/api/v1/guest-operations/reservations/{reservationId}/late-checkout", token,
            new { requestedCheckOutAt = checkOutAt.AddHours(3) });

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "an ADMIN caller must pass real policy resolution and reach the real command handler — this proves the real " +
            "IReservationScheduleReader/ILateCheckoutPolicyReader Infrastructure implementations, never a fake");
        var body = await response.Content.ReadFromJsonAsync<LateCheckoutResponseShape>();
        body!.Status.Should().Be("denied");
        body.DenialReasonCode.Should().Be("policy_not_configured");
    }

    [Fact]
    public async Task LateCheckout_for_a_different_tenants_reservation_returns_404_never_leaking_existence()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var ownerTenantId = Guid.NewGuid();
        var otherTenantId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var checkOutAt = now.AddDays(5);
        var reservationId = await SeedConfirmedReservationAsync(ownerTenantId, now.AddDays(3), checkOutAt);
        var operationId = await SeedActiveGuestStayOperationAsync(ownerTenantId, reservationId, Guid.NewGuid());
        await CheckInOperationAsync(ownerTenantId, operationId);
        var otherTenantToken = await GenerateTokenAsync(host, Guid.NewGuid(), otherTenantId, ["ADMIN"]);

        var response = await PostJsonAsync(client, $"/api/v1/guest-operations/reservations/{reservationId}/late-checkout", otherTenantToken,
            new { requestedCheckOutAt = checkOutAt.AddHours(3) });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private async Task CheckInOperationAsync(Guid tenantId, Guid operationId)
    {
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);

        var options = new DbContextOptionsBuilder<GuestOperationsDbContext>()
            .UseNpgsql(_appConnectionString, npgsqlOptions => npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "guest_operations"))
            .Options;
        await using var dbContext = new GuestOperationsDbContext(options, tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        var operation = await dbContext.GuestStayOperations.FirstAsync(o => o.Id == operationId);
        operation.CheckIn(DateTimeOffset.UtcNow);
        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();
    }

    private sealed record EarlyCheckInResponseShape(
        Guid Id, Guid ReservationId, DateTimeOffset RequestedCheckInAt, string Status, string? DenialReasonCode,
        DateTimeOffset CreatedAtUtc, DateTimeOffset? DecidedAtUtc, DateTimeOffset UpdatedAtUtc);

    private sealed record LateCheckoutResponseShape(
        Guid Id, Guid ReservationId, DateTimeOffset RequestedCheckOutAt, string ChargeType, decimal? ChargeValue, bool RequiresPix,
        string Status, string? DenialReasonCode, DateTimeOffset CreatedAtUtc, DateTimeOffset? DecidedAtUtc, DateTimeOffset UpdatedAtUtc);
}
