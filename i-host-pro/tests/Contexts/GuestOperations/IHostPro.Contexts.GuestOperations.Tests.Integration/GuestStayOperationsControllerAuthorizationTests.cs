using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using FluentAssertions;
using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Infrastructure.Messaging;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.BuildingBlocks.Infrastructure.Persistence;
using IHostPro.Contexts.GuestOperations.Api.Controllers;
using IHostPro.Contexts.GuestOperations.Domain;
using IHostPro.Contexts.GuestOperations.Infrastructure;
using IHostPro.Contexts.GuestOperations.Infrastructure.Persistence;
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

namespace IHostPro.Contexts.GuestOperations.Tests.Integration;

/// <summary>
/// Fase 10, Checkpoint 2 (Check-in/Checkout Core) — proves
/// <see cref="GuestStayOperationsController"/> against the REAL ASP.NET Core
/// HTTP + authorization pipeline (real JWT bearer validation, real
/// <c>GUEST_OPERATIONS:MANAGE</c> policy resolution, real
/// <see cref="Microsoft.AspNetCore.Authorization.IAuthorizationService"/>) —
/// mirrors <c>WhatsAppIntegrationEndpointsAuthorizationTests</c>'/
/// <c>ReservationsEndpointsTests</c>' own host-building structure. Unlike
/// WhatsApp's controller, check-in/checkout DO publish a real Integration
/// Event through Guest Operations' own durable outbox
/// (<c>IGuestOperationsTransactionExecutor</c> requires
/// <c>IDbContextOutbox&lt;GuestOperationsDbContext&gt;</c>), so this host
/// also enrolls Guest Operations' ancillary Postgres outbox — mirrors
/// <c>ReservationsEndpointsTests</c>' own precedent exactly. No RabbitMQ
/// transport is configured: nothing here asserts on message delivery, only
/// on the real HTTP authorization/business outcome, so an unrouted outbox
/// message is fine (never delivered, never asserted on).
/// </summary>
public class GuestStayOperationsControllerAuthorizationTests : IClassFixture<GuestStayOperationsControllerAuthorizationTests.Fixture>
{
    private const string GuestOperationsOutboxSchema = "guest_operations_messaging";
    private const string MainSchema = "platform_messaging";
    private const string Issuer = "https://identity.ihostpro.test";
    private const string Audience = "ihostpro-api-test";

    private readonly string _migratorConnectionString;
    private readonly string _appConnectionString;
    private readonly string _signingKeyPem;

    public GuestStayOperationsControllerAuthorizationTests(Fixture fixture)
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

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string route, string? token) =>
        SendAsync(client, HttpMethod.Post, route, token);

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string route, string? token)
    {
        var request = new HttpRequestMessage(method, route);
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return await client.SendAsync(request);
    }

    private async Task<Guid> SeedActiveGuestStayOperationAsync(Guid tenantId, Guid reservationId)
    {
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);

        var options = new DbContextOptionsBuilder<GuestOperationsDbContext>()
            .UseNpgsql(_appConnectionString, npgsqlOptions => npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "guest_operations"))
            .Options;
        await using var dbContext = new GuestOperationsDbContext(options, tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        var operation = GuestStayOperation.Create(Guid.NewGuid(), tenantId, reservationId, Guid.NewGuid(), DateTimeOffset.UtcNow);
        dbContext.GuestStayOperations.Add(operation);
        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();

        return operation.Id;
    }

    private static async Task SetTenantAsync(DbContext dbContext, Guid tenantId) =>
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.tenant_id', {tenantId.ToString()}, true)");

    // ---- Tests ------------------------------------------------------------

    [Fact]
    public async Task CheckIn_without_a_token_returns_401()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();

        var response = await PostAsync(client, $"/api/v1/guest-operations/reservations/{Guid.NewGuid()}/check-in", token: null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task CheckIn_with_a_role_lacking_GUEST_OPERATIONS_MANAGE_returns_403_never_500()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), Guid.NewGuid(), ["AI_AGENT"]);

        var response = await PostAsync(client, $"/api/v1/guest-operations/reservations/{Guid.NewGuid()}/check-in", token);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "a role without GUEST_OPERATIONS:MANAGE must be cleanly denied — never a 500 from an unregistered policy");
    }

    [Fact]
    public async Task CheckIn_then_CheckOut_as_ADMIN_via_real_HTTP_succeed_and_transition_the_real_GuestStayOperation()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var tenantId = Guid.NewGuid();
        var reservationId = Guid.NewGuid();
        await SeedActiveGuestStayOperationAsync(tenantId, reservationId);
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), tenantId, ["ADMIN"]);

        var checkInResponse = await PostAsync(client, $"/api/v1/guest-operations/reservations/{reservationId}/check-in", token);
        checkInResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            "an ADMIN caller must pass real policy resolution and reach the real command handler");
        var checkInBody = await checkInResponse.Content.ReadFromJsonAsync<GuestStayOperationResponseShape>();
        checkInBody!.Status.Should().Be("checked_in");

        var checkOutResponse = await PostAsync(client, $"/api/v1/guest-operations/reservations/{reservationId}/checkout", token);
        checkOutResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var checkOutBody = await checkOutResponse.Content.ReadFromJsonAsync<GuestStayOperationResponseShape>();
        checkOutBody!.Status.Should().Be("checked_out");
    }

    [Fact]
    public async Task CheckIn_called_twice_as_ADMIN_is_idempotent_and_returns_200_both_times()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var tenantId = Guid.NewGuid();
        var reservationId = Guid.NewGuid();
        await SeedActiveGuestStayOperationAsync(tenantId, reservationId);
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), tenantId, ["ADMIN"]);

        var firstResponse = await PostAsync(client, $"/api/v1/guest-operations/reservations/{reservationId}/check-in", token);
        var secondResponse = await PostAsync(client, $"/api/v1/guest-operations/reservations/{reservationId}/check-in", token);

        firstResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        secondResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            "a redelivered check-in for an already-CheckedIn operation must be a silent idempotent no-op, never an error");
        var secondBody = await secondResponse.Content.ReadFromJsonAsync<GuestStayOperationResponseShape>();
        secondBody!.Status.Should().Be("checked_in");
    }

    [Fact]
    public async Task CheckIn_for_a_different_tenants_reservation_returns_404_never_leaking_existence()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var ownerTenantId = Guid.NewGuid();
        var otherTenantId = Guid.NewGuid();
        var reservationId = Guid.NewGuid();
        await SeedActiveGuestStayOperationAsync(ownerTenantId, reservationId);
        var otherTenantToken = await GenerateTokenAsync(host, Guid.NewGuid(), otherTenantId, ["ADMIN"]);

        var response = await PostAsync(client, $"/api/v1/guest-operations/reservations/{reservationId}/check-in", otherTenantToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "a different tenant's RLS-scoped connection must never see this tenant's GuestStayOperation");
    }

    private sealed record GuestStayOperationResponseShape(
        Guid Id, Guid ReservationId, Guid PropertyId, string Status,
        DateTimeOffset? CheckedInAtUtc, DateTimeOffset? CheckedOutAtUtc,
        DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);
}
