using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Infrastructure.Messaging;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.Contexts.Identity.Api.Contracts;
using IHostPro.Contexts.Identity.Application;
using IHostPro.Contexts.Identity.Contracts;
using IHostPro.Contexts.Identity.Domain;
using IHostPro.Contexts.Identity.Domain.ValueObjects;
using IHostPro.Contexts.Identity.Infrastructure.Persistence;
using IHostPro.Contexts.Identity.Infrastructure.Security;
using IHostPro.Contexts.PropertyManagement.Api.Contracts;
using IHostPro.Contexts.PropertyManagement.Application;
using IHostPro.Contexts.PropertyManagement.Contracts;
using IHostPro.Contexts.PropertyManagement.Infrastructure.Persistence;
using IHostPro.Contexts.Reservations.Application;
using IHostPro.Contexts.Reservations.Application.Reservations;
using IHostPro.Contexts.Reservations.Infrastructure.Persistence;
using JasperFx;
using JasperFx.Resources;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Npgsql;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.Persistence.Durability;
using Wolverine.Postgresql;
using Wolverine.RabbitMQ;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace IHostPro.Api.Tests.Integration;

/// <summary>
/// Regression test for the Wolverine Main/Ancillary message-store defect
/// found during Checkpoint 6 (Fase 2, Incremento 1) homologação: the REAL
/// <c>IHostPro.Api</c> host, as originally written, registered two
/// <see cref="MessageStoreRole.Ancillary"/> PostgreSQL stores
/// (<c>identity_messaging</c>/<c>property_management_messaging</c>) with no
/// <see cref="MessageStoreRole.Main"/> store, which made Wolverine throw
/// <c>InvalidWolverineStorageConfigurationException</c> at startup — a defect
/// no automated test had ever caught, because every other test file in this
/// solution hand-assembles a PARTIAL host that enrolls at most one of the two
/// ancillary stores at a time, never composing the real <c>Program.cs</c>.
///
/// This file exercises the REAL composition root via
/// <see cref="WebApplicationFactory{TEntryPoint}"/> against
/// <c>IHostPro.Api</c>'s own <c>Program</c> — no service is re-registered by
/// hand here. It proves: (1) the host starts without throwing; (2) exactly
/// one Main store (<c>platform_messaging</c>) and exactly two Ancillary
/// stores (<c>identity_messaging</c>/<c>property_management_messaging</c>)
/// exist, in three distinct schemas; (3) real Identity commands
/// (<see cref="LoginCommand"/>, <c>CreateUserCommand</c>) durably persist
/// their events into <c>identity_messaging</c> only; (4) a real Property
/// Management command (<see cref="CreateCondominiumCommand"/>) durably
/// persists its event into <c>property_management_messaging</c> only; (5)
/// <c>platform_messaging</c> never receives a domain event — consistent with
/// no <c>DbContext</c> ever being <c>.Enroll()</c>-ed into it; (6) the host
/// shuts down cleanly.
///
/// Envelope-persistence assertions (3)/(4) are proven with RabbitMQ
/// genuinely stopped (real <c>docker stop</c> via Testcontainers'
/// <c>StopAsync</c> — not <c>docker pause</c>), never with the broker
/// reachable. An earlier version of this test asserted envelope presence
/// immediately after an HTTP call with the broker still up and observed
/// ZERO rows. Two competing explanations were possible: (a) a race, where
/// Wolverine's synchronous first delivery attempt (awaited inline,
/// immediately after the DB commit — see <c>Program.cs</c>'s
/// <c>CircuitBreaking(cb => cb.FailuresBeforeCircuitBreaks = 1)</c> comment)
/// succeeds and deletes the durable-outbox row before the test's own
/// follow-up query runs; or (b) a genuine persistence defect. Stopping
/// RabbitMQ first (connection refused is near-instant per
/// <see cref="IHostPro.BuildingBlocks.Infrastructure.Messaging.RabbitMqClientTimeoutOptions"/>'s
/// own doc comment, which records the same near-instant-failure behavior
/// with the broker fully stopped) removes the delivery race entirely, so
/// persistence and delivery are observed as two separate, ordered steps.
/// See the test method's own body for which explanation the evidence
/// supports.
/// </summary>
public class WolverineThreeStoreCompositionTests : IClassFixture<WolverineThreeStoreCompositionTests.Fixture>
{
    private const string IdentityOutboxSchema = "identity_messaging";
    private const string PropertyManagementOutboxSchema = "property_management_messaging";
    private const string ReservationsOutboxSchema = "reservations_messaging";
    private const string PlatformMessagingSchema = "platform_messaging";
    private const string KnownPassword = "Correct-Horse-Battery-Staple-42!";

    /// <summary>
    /// Dedicated RabbitMQ virtual host, on the SAME broker container as the
    /// default "/" vhost, used exclusively by
    /// <see cref="Reservations_events_survive_a_rabbitmq_outage_and_deliver_on_recovery_through_the_same_provisioned_topology"/>.
    /// Exchanges are scoped per-vhost in RabbitMQ, so this test's own
    /// <c>reservation-events</c> exchange never exists in, and never leaks
    /// into, the default vhost the topology test
    /// (<see cref="MigrationRunner_provisions_rabbitmq_topology_idempotently_and_the_real_host_delivers_through_it"/>)
    /// asserts against — the two tests are isolated regardless of execution
    /// order, without requiring a second container (infeasible here: the
    /// real, unmodified <c>Program.cs</c> has no RabbitMQ port override, so
    /// the shared container already owns the fixed host port 5672 for the
    /// whole class lifetime).
    /// </summary>
    private const string ReservationsOutageVirtualHost = "/reservations-outage-test";

    private readonly Fixture _fixture;

    public WolverineThreeStoreCompositionTests(Fixture fixture) => _fixture = fixture;

    public sealed class Fixture : IAsyncLifetime
    {
        private const string AppRolePassword = "test_app_password";
        private const string MigratorRolePassword = "test_migrator_password";

        private PostgreSqlContainer _postgresContainer = null!;
        private RabbitMqContainer _rabbitMqContainer = null!;
        public string MigratorConnectionString { get; private set; } = null!;
        public string AppConnectionString { get; private set; } = null!;
        public RabbitMqContainer RabbitMq => _rabbitMqContainer;

        public async Task InitializeAsync()
        {
            _postgresContainer = new PostgreSqlBuilder()
                .WithImage("postgres:16")
                .WithDatabase("ihostpro_test")
                .WithUsername("ihostpro")
                .WithPassword("ihostpro_dev")
                .Build();
            await _postgresContainer.StartAsync();

            // WolverineConfigurationExtensions.UseIHostProRabbitMq (the REAL
            // Program.cs's own RabbitMQ wiring, exercised unmodified here) has
            // no port override — it always connects on RabbitMQ's default
            // AMQP port 5672. Every OTHER test file in this solution sidesteps
            // this by calling opts.UseRabbitMq(...) directly with the
            // container's randomly-mapped port; this file cannot do that
            // without hand-reproducing production wiring, which the whole
            // point of this regression test forbids. A fixed host port
            // binding is the only way to exercise the real, unmodified
            // Program.cs against a real (if here, ephemeral) broker.
            _rabbitMqContainer = new RabbitMqBuilder()
                .WithImage("rabbitmq:3-management-alpine")
                .WithPortBinding(5672, 5672)
                .Build();
            await _rabbitMqContainer.StartAsync();
            await CreateReservationsOutageVirtualHostAsync();

            var adminConnectionString = _postgresContainer.GetConnectionString();

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
            await using (var pmDbContext = CreatePropertyManagementDbContext(MigratorConnectionString))
            {
                await pmDbContext.Database.MigrateAsync();
            }
            await using (var reservationsDbContext = CreateReservationsDbContext(MigratorConnectionString))
            {
                await reservationsDbContext.Database.MigrateAsync();
            }

            // Mirrors IHostPro.MigrationRunner exactly: platform_messaging
            // (Main) provisioned first, then the three Ancillary stores — same
            // SetupResources()/GRANT shape.
            await ProvisionMessageStoreSchemaAsync(PlatformMessagingSchema, dbContextType: null);
            await ProvisionMessageStoreSchemaAsync(IdentityOutboxSchema, typeof(IdentityDbContext));
            await ProvisionMessageStoreSchemaAsync(PropertyManagementOutboxSchema, typeof(PropertyManagementDbContext));
            await ProvisionMessageStoreSchemaAsync(ReservationsOutboxSchema, typeof(ReservationsDbContext));

            await using (var verifyConnection = new NpgsqlConnection(MigratorConnectionString))
            {
                await verifyConnection.OpenAsync();
                await using var verifyCommand = verifyConnection.CreateCommand();
                verifyCommand.CommandText = "SELECT table_schema, table_name FROM information_schema.tables WHERE table_schema IN ('platform_messaging','identity_messaging','property_management_messaging','reservations_messaging') ORDER BY table_schema, table_name";
                await using var reader = await verifyCommand.ExecuteReaderAsync();
                var found = new List<string>();
                while (await reader.ReadAsync())
                    found.Add($"{reader.GetString(0)}.{reader.GetString(1)}");
                if (found.Count == 0)
                    throw new InvalidOperationException("DIAGNOSTIC: no message-store tables found after provisioning at all.");
                if (!found.Any(f => f.StartsWith("platform_messaging.")))
                    throw new InvalidOperationException("DIAGNOSTIC: platform_messaging has NO tables after provisioning. Found: " + string.Join(", ", found));
            }
        }

        public async Task DisposeAsync()
        {
            await _rabbitMqContainer.DisposeAsync();
            await _postgresContainer.DisposeAsync();
        }

        /// <summary>
        /// Creates <see cref="ReservationsOutageVirtualHost"/> on the shared
        /// broker container via <c>rabbitmqctl</c> (executed inside the
        /// container — no dependency on the management HTTP port being
        /// mapped) and grants the default test user full configure/write/read
        /// permissions on it. Runs once, at fixture start, before any test
        /// connects.
        /// </summary>
        private async Task CreateReservationsOutageVirtualHostAsync()
        {
            var addVhost = await _rabbitMqContainer.ExecAsync(
                ["rabbitmqctl", "add_vhost", ReservationsOutageVirtualHost]);
            if (addVhost.ExitCode != 0)
                throw new InvalidOperationException(
                    $"Failed to create RabbitMQ vhost '{ReservationsOutageVirtualHost}': {addVhost.Stderr}");

            var setPermissions = await _rabbitMqContainer.ExecAsync(
                ["rabbitmqctl", "set_permissions", "-p", ReservationsOutageVirtualHost, RabbitMqBuilder.DefaultUsername, ".*", ".*", ".*"]);
            if (setPermissions.ExitCode != 0)
                throw new InvalidOperationException(
                    $"Failed to grant permissions on RabbitMQ vhost '{ReservationsOutageVirtualHost}': {setPermissions.Stderr}");
        }

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

        private static ReservationsDbContext CreateReservationsDbContext(string connectionString)
        {
            var options = new DbContextOptionsBuilder<ReservationsDbContext>()
                .UseNpgsql(connectionString, npgsqlOptions =>
                    npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "reservations"))
                .Options;
            return new ReservationsDbContext(options, new TenantContext());
        }

        private async Task ProvisionMessageStoreSchemaAsync(string schema, Type? dbContextType)
        {
            var hostBuilder = Host.CreateApplicationBuilder();
            hostBuilder.UseWolverine(opts =>
            {
                if (dbContextType is null)
                {
                    // Main store — default MessageStoreRole is Main; never
                    // enrolled to any DbContext.
                    opts.PersistMessagesWithPostgresql(MigratorConnectionString, schema);
                }
                else
                {
                    opts.EnrollAncillaryPostgresqlOutbox(MigratorConnectionString, schema, dbContextType);
                }
                opts.AutoBuildMessageStorageOnStartup = AutoCreate.None;
                opts.UseEntityFrameworkCoreTransactions();
            });

            using var setupHost = hostBuilder.Build();
            await setupHost.SetupResources();

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

    // ---- Real composition root ---------------------------------------------

    /// <summary>
    /// Environment variables, never <c>ConfigureAppConfiguration</c> —
    /// confirmed empirically that <see cref="WebApplicationFactory{TEntryPoint}"/>'s
    /// <c>ConfigureAppConfiguration</c> customization does not reliably
    /// override <c>Program.cs</c>'s OWN <c>WebApplication.CreateBuilder()</c>
    /// default configuration sources for this Minimal-Hosting-style entry
    /// point: without this fix, the real host silently fell back to the
    /// committed <c>appsettings.json</c> (pointing at the developer's own
    /// local Postgres on port 5432) instead of this test's ephemeral
    /// Testcontainers database, surfacing as a confusing
    /// <c>ObjectDisposedException</c> from inside
    /// <see cref="WebApplicationFactory{TEntryPoint}.StartServer"/> (the real
    /// failure — <c>relation "platform_messaging.wolverine_nodes" does not
    /// exist</c> — was only visible in the process's own Serilog console
    /// output, never propagated to the test). Environment variables are
    /// loaded by <c>WebApplication.CreateBuilder()</c> itself, with the
    /// highest precedence of any default source, so this is immune to that
    /// composition-timing issue. Removed in <see cref="ResetEnvironment"/> —
    /// this test owns the process's environment for its own duration only
    /// (this project has a single test class/method, run in its own
    /// process).
    /// </summary>
    private static readonly string[] EnvironmentKeys =
    [
        "ConnectionStrings__Identity", "ConnectionStrings__PropertyManagement", "ConnectionStrings__Reservations",
        "ConnectionStrings__Configuration", "ConnectionStrings__Housekeeping", "ConnectionStrings__Platform",
        "Identity__Jwt__Issuer", "Identity__Jwt__Audience", "Identity__Jwt__AccessTokenLifetime", "Identity__Jwt__ClockSkew",
        "Identity__Jwt__SigningKey__PrivateKeyPem",
        "Identity__AccountLockout__MaxFailedAccessAttempts", "Identity__AccountLockout__DefaultLockoutDuration", "Identity__AccountLockout__AllowedForNewUsers",
        "Identity__RefreshToken__Lifetime", "Identity__RefreshToken__SecretSizeBytes", "Identity__RefreshToken__ConcurrentRotationGraceWindow",
        "RabbitMq__Host", "RabbitMq__VirtualHost", "RabbitMq__Username", "RabbitMq__Password",
        "OpenTelemetry__OtlpEndpoint",
        "ASPNETCORE_ENVIRONMENT",
    ];

    private WebApplicationFactory<Program> BuildFactory(string rabbitMqVirtualHost = "/")
    {
        using var signingKey = System.Security.Cryptography.RSA.Create(2048);
        var signingKeyPem = signingKey.ExportRSAPrivateKeyPem();

        var values = new Dictionary<string, string?>
        {
            ["ConnectionStrings__Identity"] = _fixture.AppConnectionString,
            ["ConnectionStrings__PropertyManagement"] = _fixture.AppConnectionString,
            ["ConnectionStrings__Reservations"] = _fixture.AppConnectionString,
            ["ConnectionStrings__Configuration"] = _fixture.AppConnectionString,
            ["ConnectionStrings__Housekeeping"] = _fixture.AppConnectionString,
            ["ConnectionStrings__Platform"] = _fixture.AppConnectionString,
            ["Identity__Jwt__Issuer"] = "https://identity.ihostpro.test",
            ["Identity__Jwt__Audience"] = "ihostpro-api-test",
            ["Identity__Jwt__AccessTokenLifetime"] = "00:15:00",
            ["Identity__Jwt__ClockSkew"] = "00:01:00",
            ["Identity__Jwt__SigningKey__PrivateKeyPem"] = signingKeyPem,
            ["Identity__AccountLockout__MaxFailedAccessAttempts"] = "5",
            ["Identity__AccountLockout__DefaultLockoutDuration"] = "00:05:00",
            ["Identity__AccountLockout__AllowedForNewUsers"] = "true",
            ["Identity__RefreshToken__Lifetime"] = "30.00:00:00",
            ["Identity__RefreshToken__SecretSizeBytes"] = "32",
            ["Identity__RefreshToken__ConcurrentRotationGraceWindow"] = "00:00:10",
            ["RabbitMq__Host"] = _fixture.RabbitMq.Hostname,
            ["RabbitMq__VirtualHost"] = rabbitMqVirtualHost,
            ["RabbitMq__Username"] = RabbitMqBuilder.DefaultUsername,
            ["RabbitMq__Password"] = RabbitMqBuilder.DefaultPassword,
            // Wolverine.RabbitMQ has no separate Port config key
            // (WolverineConfigurationExtensions.UseIHostProRabbitMq) — this
            // test fixes RabbitMQ's own container to the default AMQP host
            // port (5672) precisely so the real, unmodified Program.cs (which
            // never reads a port override) still connects successfully.
            ["OpenTelemetry__OtlpEndpoint"] = "http://localhost:14317",
            ["ASPNETCORE_ENVIRONMENT"] = "Development",
            ["DOTNET_ENVIRONMENT"] = "Development",
        };

        foreach (var (key, value) in values)
            Environment.SetEnvironmentVariable(key, value);

        return new WebApplicationFactory<Program>();
    }

    private static void ResetEnvironment()
    {
        foreach (var key in EnvironmentKeys)
            Environment.SetEnvironmentVariable(key, null);
    }

    // ---- Seeding (Identity) --------------------------------------------------

    private async Task<Guid> SeedTenantAsync(string slug)
    {
        var tenantId = Guid.NewGuid();
        await using var dbContext = CreateIdentityDbContext(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);

        var tenant = Tenant.Provision(tenantId, TenantSlug.Create(slug), "Test Tenant", DateTimeOffset.UtcNow);
        dbContext.Tenants.Add(tenant);

        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();
        return tenantId;
    }

    private async Task<(Guid UserId, string Email)> SeedAdminUserAsync(Guid tenantId)
    {
        await using var dbContext = CreateIdentityDbContext(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);

        var hasher = new Argon2PasswordHasher(new KonsciousArgon2idPrimitive(), Options.Create(new Argon2Options()));
        var hash = PasswordHash.FromEncoded(hasher.HashPassword(null!, KnownPassword));
        var now = DateTimeOffset.UtcNow;
        var email = $"{Guid.NewGuid():N}@ihostpro.com";
        var user = User.Register(Guid.NewGuid(), tenantId, Email.Create(email), "Composition Test Admin", hash, now);
        dbContext.Users.Add(user);
        // ADMIN — needed for the login token to also carry PROPERTIES:MANAGE
        // (Condominium/Property creation) so this one HTTP-driven scenario
        // exercises both Bounded Contexts through the SAME real login token.
        dbContext.UserRoles.Add(new UserRole(tenantId, user.Id, "ADMIN", now, assignedByUserId: null));

        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();
        return (user.Id, email);
    }

    private IdentityDbContext CreateIdentityDbContext(Guid tenantId)
    {
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);
        var options = new DbContextOptionsBuilder<IdentityDbContext>()
            .UseNpgsql(_fixture.AppConnectionString, npgsqlOptions =>
                npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "identity"))
            .Options;
        return new IdentityDbContext(options, tenantContext);
    }

    private static async Task SetPostgresTenantAsync(IdentityDbContext dbContext, Guid tenantId) =>
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.tenant_id', {tenantId.ToString()}, true)");

    // ---- The test -----------------------------------------------------------

    private static readonly JsonSerializerOptions JsonWebDefaults = new(JsonSerializerDefaults.Web);
    private static readonly AddressRequest ValidAddress = new(
        "59090-000", "Rua Exemplo", "100", "Bloco A", "Ponta Negra", "Natal", "RN", "BR");

    [Fact]
    public async Task The_real_host_starts_with_both_dispatchers_and_processes_HTTP_requests_from_both_contexts_correctly()
    {
        var tenantSlug = $"tenant-{Guid.NewGuid():N}"[..20];
        var tenantId = await SeedTenantAsync(tenantSlug);
        var (_, email) = await SeedAdminUserAsync(tenantId);

        try
        {
            await RunAsync(tenantSlug, email);
        }
        finally
        {
            ResetEnvironment();
        }
    }

    private async Task RunAsync(string tenantSlug, string email)
    {
        using var factory = BuildFactory();

        // ---- 1. Host starts without InvalidWolverineStorageConfigurationException ----
        var runtime = factory.Services.GetRequiredService<IWolverineRuntime>();

        // ---- Wolverine store separation: exactly one Main, exactly two Ancillary, three distinct schemas ----
        runtime.Storage.Role.Should().Be(MessageStoreRole.Main);
        runtime.Storage.Uri.ToString().Should().Contain(PlatformMessagingSchema);

        var identityStore = runtime.FindAncillaryStoreForMarkerType(typeof(IdentityDbContext));
        identityStore.Should().NotBeNull();
        identityStore.Role.Should().Be(MessageStoreRole.Ancillary);
        identityStore.Uri.ToString().Should().Contain(IdentityOutboxSchema);

        var pmStore = runtime.FindAncillaryStoreForMarkerType(typeof(PropertyManagementDbContext));
        pmStore.Should().NotBeNull();
        pmStore.Role.Should().Be(MessageStoreRole.Ancillary);
        pmStore.Uri.ToString().Should().Contain(PropertyManagementOutboxSchema);

        var reservationsStore = runtime.FindAncillaryStoreForMarkerType(typeof(ReservationsDbContext));
        reservationsStore.Should().NotBeNull();
        reservationsStore.Role.Should().Be(MessageStoreRole.Ancillary);
        reservationsStore.Uri.ToString().Should().Contain(ReservationsOutboxSchema);

        new[] { runtime.Storage.Uri.ToString(), identityStore.Uri.ToString(), pmStore.Uri.ToString(), reservationsStore.Uri.ToString() }
            .Should().OnlyHaveUniqueItems("the four stores must live in four genuinely distinct schemas");

        // ---- All three dispatchers are registered, unambiguously, in the real container ----
        // (all Scoped — resolved from a scope, never the root provider,
        // exactly like every real request would.)
        using (var scope = factory.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<IHostPro.Contexts.Identity.Application.IIdentityRequestDispatcher>().Should().NotBeNull();
            scope.ServiceProvider.GetRequiredService<IHostPro.Contexts.PropertyManagement.Application.IPropertyManagementRequestDispatcher>().Should().NotBeNull();
            scope.ServiceProvider.GetRequiredService<IReservationsRequestDispatcher>().Should().NotBeNull();
        }

        using var client = factory.CreateClient();

        // ---- Deterministic proof setup: stop RabbitMQ BEFORE any HTTP call
        // that is expected to publish an event. Real docker stop (Testcontainers
        // StopAsync), never docker pause: a stopped broker makes any new/reused
        // connection attempt fail with an immediate connection-refused/reset,
        // whereas a paused broker would instead exercise the (much slower)
        // silent-network-partition path — the two are deliberately different
        // failure shapes and only the former gives a deterministic, fast
        // baseline for this proof. ----
        await _fixture.RabbitMq.StopAsync();

        (await CountAllEnvelopesAsync(IdentityOutboxSchema)).Should().Be(0, "no command has run yet");
        (await CountAllEnvelopesAsync(PropertyManagementOutboxSchema)).Should().Be(0, "no command has run yet");
        (await CountAllEnvelopesAsync(PlatformMessagingSchema)).Should().Be(0, "no command has run yet");

        // ---- 1. Login (Identity) — broker unreachable. Must still succeed:
        // the durable outbox persists inside the same DB transaction as the
        // command's own writes; delivery is a separate, best-effort step
        // (Program.cs's CircuitBreaking(1) + RabbitMqClientTimeoutOptions'
        // fast-fail tuning). LoginCommandHandler does publish UserLoggedIn on
        // success (confirmed by reading its source directly), but per this
        // investigation's instructions Login alone is not used as the only
        // proof — CreateUser below is the preferred proof for Identity. ----
        var loginResponse = await PostAsync(client, "/api/v1/auth/login", new LoginRequest(tenantSlug, email, KnownPassword), token: null);
        loginResponse.StatusCode.Should().Be(HttpStatusCode.OK, "durable-outbox persistence must not depend on broker reachability");
        var tokens = await loginResponse.Content.ReadFromJsonAsync<AuthTokensResponse>(JsonWebDefaults);
        var token = tokens!.AccessToken;

        // ---- 2. Identity: CreateUser — preferred proof (publishes UserCreated + UserRoleAssigned) ----
        var createUserResponse = await PostAsync(
            client,
            "/api/v1/users",
            new CreateUserRequest("Composition Test Operator", $"{Guid.NewGuid():N}@ihostpro.com", "Correct-Horse-Battery-Staple-99!", "OPERATOR"),
            token);
        createUserResponse.StatusCode.Should().Be(HttpStatusCode.Created, "CreateUserCommandHandler must persist its events even with the broker unreachable");

        // ---- 3. Property Management: CreateCondominium (publishes CondominiumCreated) ----
        var createCondominiumResponse = await PostAsync(client, "/api/v1/condominiums", new CreateCondominiumRequest("Composition Test Condo", ValidAddress), token);
        createCondominiumResponse.StatusCode.Should().Be(HttpStatusCode.Created, "CreateCondominiumCommandHandler must persist its event even with the broker unreachable");

        // ---- With RabbitMQ STILL stopped: direct PostgreSQL inspection.
        // DIAGNOSTIC: gathered as a single report (not asserted incrementally)
        // so one run yields the full cross-schema picture instead of aborting
        // at the first failing schema. ----
        var identityEnvelopesDown = await DumpAllEnvelopesAsync(IdentityOutboxSchema);
        var pmEnvelopesDown = await DumpAllEnvelopesAsync(PropertyManagementOutboxSchema);
        var platformEnvelopesDown = await DumpAllEnvelopesAsync(PlatformMessagingSchema);
        var identityUserCount = await CountRowsAsync(_fixture.MigratorConnectionString, "identity", "users");
        var identityUserRoleCount = await CountRowsAsync(_fixture.MigratorConnectionString, "identity", "user_roles");
        var pmCondominiumCount = await CountRowsAsync(_fixture.MigratorConnectionString, "property_management", "condominiums");

        var report =
            $"identity_messaging envelopes: [{string.Join(", ", identityEnvelopesDown)}] (count={identityEnvelopesDown.Count}); " +
            $"property_management_messaging envelopes: [{string.Join(", ", pmEnvelopesDown)}] (count={pmEnvelopesDown.Count}); " +
            $"platform_messaging envelopes: [{string.Join(", ", platformEnvelopesDown)}] (count={platformEnvelopesDown.Count}); " +
            $"identity.users rows={identityUserCount}; identity.user_roles rows={identityUserRoleCount}; " +
            $"property_management.condominiums rows={pmCondominiumCount}";

        var identityHasAllThreeEvents =
            identityEnvelopesDown.Contains("IHostPro.Contexts.Identity.Contracts.UserLoggedIn") &&
            identityEnvelopesDown.Contains("IHostPro.Contexts.Identity.Contracts.UserCreated") &&
            identityEnvelopesDown.Contains("IHostPro.Contexts.Identity.Contracts.UserRoleAssigned");
        var pmHasCondominiumCreated = pmEnvelopesDown.Contains("IHostPro.Contexts.PropertyManagement.Contracts.CondominiumCreated");

        (identityHasAllThreeEvents && pmHasCondominiumCreated).Should().BeTrue(report);

        // ---- Isolation still holds with the broker down ----
        (await CountAllEnvelopesAsync(PlatformMessagingSchema)).Should().Be(0,
            "platform_messaging holds only Wolverine's own runtime-coordination tables — no DbContext is ever enrolled into it");
        (await CountEnvelopesAsync(PropertyManagementOutboxSchema, "IHostPro.Contexts.Identity.Contracts.UserLoggedIn")).Should().Be(0);
        (await CountEnvelopesAsync(PropertyManagementOutboxSchema, "IHostPro.Contexts.Identity.Contracts.UserCreated")).Should().Be(0);
        (await CountEnvelopesAsync(IdentityOutboxSchema, "IHostPro.Contexts.PropertyManagement.Contracts.CondominiumCreated")).Should().Be(0);

        // ---- Minimal envelope content inspection (message_type + destination
        // only — never `body`, which may carry tenant/actor identifiers) ----
        var userCreatedEnvelopes = await DumpEnvelopeMetadataAsync(IdentityOutboxSchema, "IHostPro.Contexts.Identity.Contracts.UserCreated");
        userCreatedEnvelopes.Should().ContainSingle();
        userCreatedEnvelopes[0].Destination.Should().Contain("identity-events", "UserCreated must route to Identity's own exchange, never Property Management's");

        var condominiumCreatedEnvelopes = await DumpEnvelopeMetadataAsync(PropertyManagementOutboxSchema, "IHostPro.Contexts.PropertyManagement.Contracts.CondominiumCreated");
        condominiumCreatedEnvelopes.Should().ContainSingle();
        condominiumCreatedEnvelopes[0].Destination.Should().Contain("property-management-events", "CondominiumCreated must route to Property Management's own exchange, never Identity's");

        // ---- Restore the broker for cleanup (no delivery assertions here —
        // outage/recovery with a bound test queue and full assertions is
        // covered by MigrationRunner_provisions_rabbitmq_topology_idempotently_and_the_real_host_delivers_through_it). ----
        await _fixture.RabbitMq.StartAsync();

        // ---- Clean shutdown ----
        var dispose = () => factory.Dispose();
        dispose.Should().NotThrow();
    }

    // ---- RabbitMQ messaging topology provisioning (Checkpoint 6, third defect root cause) ----

    // Checked against the default "/" vhost only. This test's own "before"
    // assertion requires a broker with NOTHING provisioned yet in that vhost;
    // Reservations_events_survive_a_rabbitmq_outage_and_deliver_on_recovery_through_the_same_provisioned_topology
    // never touches "/" — it provisions and asserts entirely inside its own
    // ReservationsOutageVirtualHost — so the two tests share no exchange
    // state and this array is order-independent regardless of which test
    // runs first.
    private static readonly string[] MessagingExchangeNames = ["identity-events", "property-management-events", "reservation-events"];

    /// <summary>
    /// Confirms MigrationRunner provisions the RabbitMQ topology idempotently
    /// (never provisioned before it runs, exists and correct after, still
    /// correct — no duplicates/errors — after a second run), then that the
    /// real Host delivers through it (no channel-shutdown errors), and
    /// finally that outage/recovery through that SAME provisioned topology
    /// still isolates the two Ancillary Stores correctly. One Testcontainers
    /// lifecycle for all three concerns, per this investigation's own
    /// proportionality instructions.
    /// </summary>
    [Fact]
    public async Task MigrationRunner_provisions_rabbitmq_topology_idempotently_and_the_real_host_delivers_through_it()
    {
        var tenantSlug = $"tenant-{Guid.NewGuid():N}"[..20];
        var tenantId = await SeedTenantAsync(tenantSlug);
        var (_, email) = await SeedAdminUserAsync(tenantId);

        // ---- 1. Confirm the exchanges do not exist yet ----
        var before = await CheckExchangesExistAsync();
        before.Values.Should().OnlyContain(exists => exists == false, "no code path has provisioned RabbitMQ topology yet in this fresh broker");

        // ---- 2. Run MigrationRunner (first time) ----
        var (exitCode1, output1) = await RunMigrationRunnerAsync();
        exitCode1.Should().Be(0, $"MigrationRunner's first run must succeed. Output:\n{output1}");

        var afterFirstRun = await CheckExchangesExistAsync();
        afterFirstRun.Values.Should().OnlyContain(exists => exists == true, "both exchanges must exist after MigrationRunner's first run");

        // ---- 3. Run MigrationRunner again — idempotency ----
        var (exitCode2, output2) = await RunMigrationRunnerAsync();
        exitCode2.Should().Be(0, $"MigrationRunner's second run must also succeed (idempotent). Output:\n{output2}");

        var afterSecondRun = await CheckExchangesExistAsync();
        afterSecondRun.Values.Should().OnlyContain(exists => exists == true, "both exchanges must still exist after a second, idempotent run");

        // ---- 4. Real Host delivers through the now-provisioned topology, WITH
        // test-only queues bound so publish success is actually observable
        // (mandatory=false — confirmed by reading RabbitMqSender.SendAsync's
        // source directly — means an unroutable publish is silently dropped
        // by the broker with no BasicReturn/exception, so without a bound
        // queue, "delivered" and "silently discarded" are indistinguishable
        // from the outbox's own perspective). ----
        using var factory = BuildFactory();
        var runtime = factory.Services.GetRequiredService<IWolverineRuntime>();

        var identityDestinations = new[]
        {
            new Uri("rabbitmq://exchange/identity-events/routing/user_logged_in"),
            new Uri("rabbitmq://exchange/identity-events/routing/user_created"),
            new Uri("rabbitmq://exchange/identity-events/routing/user_role_assigned"),
        };
        var pmDestinations = new[] { new Uri("rabbitmq://exchange/property-management-events/routing/condominium_created") };

        await using (var probe1 = await DeclareTemporaryTestQueuesAsync())
        {
            var readiness1 = Stopwatch.StartNew();
            var ready1 = await WaitForSendingAgentsReadyAsync(runtime, identityDestinations.Concat(pmDestinations), TimeSpan.FromSeconds(30));
            readiness1.Stop();
            ready1.Should().BeTrue($"sending agents for all four destinations must become unlatched within 30s (took {readiness1.Elapsed})");

            using var client = factory.CreateClient();

            var loginResponse = await PostAsync(client, "/api/v1/auth/login", new LoginRequest(tenantSlug, email, KnownPassword), token: null);
            loginResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            var token = (await loginResponse.Content.ReadFromJsonAsync<AuthTokensResponse>(JsonWebDefaults))!.AccessToken;

            var createUserResponse = await PostAsync(
                client, "/api/v1/users",
                new CreateUserRequest("Topology Test Operator", $"{Guid.NewGuid():N}@ihostpro.com", "Correct-Horse-Battery-Staple-99!", "OPERATOR"),
                token);
            createUserResponse.StatusCode.Should().Be(HttpStatusCode.Created);

            var createCondominiumResponse = await PostAsync(client, "/api/v1/condominiums", new CreateCondominiumRequest("Topology Test Condo", ValidAddress), token);
            createCondominiumResponse.StatusCode.Should().Be(HttpStatusCode.Created);

            var deliveredWithTopology = await WaitUntilAsync(async () =>
                    (await CountAllEnvelopesAsync(IdentityOutboxSchema)) == 0 &&
                    (await CountAllEnvelopesAsync(PropertyManagementOutboxSchema)) == 0,
                TimeSpan.FromSeconds(30));

            var identityMessages1 = await DrainQueueAsync(probe1.Channel, probe1.IdentityQueue);
            var pmMessages1 = await DrainQueueAsync(probe1.Channel, probe1.PropertyManagementQueue);

            Console.WriteLine(
                $"Sending-agent readiness wait: {readiness1.Elapsed}. " +
                $"Delivered with topology+bound queues present: {deliveredWithTopology}. " +
                $"Identity test queue received: [{string.Join(", ", identityMessages1.Select(m => $"{m.RoutingKey}/{m.MessageType}"))}]. " +
                $"PM test queue received: [{string.Join(", ", pmMessages1.Select(m => $"{m.RoutingKey}/{m.MessageType}"))}].");
            Console.Out.Flush();

            deliveredWithTopology.Should().BeTrue("with the topology provisioned, a bound test queue, and real agent readiness confirmed, publishing should succeed and envelopes should clear");
            (await CountAllEnvelopesAsync(PlatformMessagingSchema)).Should().Be(0, "no domain event is ever routed to the Main store");

            identityMessages1.Should().Contain(m => m.RoutingKey == "user_logged_in" && m.MessageType == "IHostPro.Contexts.Identity.Contracts.UserLoggedIn");
            identityMessages1.Should().Contain(m => m.RoutingKey == "user_created" && m.MessageType == "IHostPro.Contexts.Identity.Contracts.UserCreated");
            identityMessages1.Should().Contain(m => m.RoutingKey == "user_role_assigned" && m.MessageType == "IHostPro.Contexts.Identity.Contracts.UserRoleAssigned");
            identityMessages1.Should().HaveCount(3, "no duplication — exactly one message per routing key");
            pmMessages1.Should().ContainSingle(m => m.RoutingKey == "condominium_created" && m.MessageType == "IHostPro.Contexts.PropertyManagement.Contracts.CondominiumCreated");

            // ---- 5. Outage/recovery through the SAME (durable, broker-persisted) exchanges ----
            // This probe queue is durable + non-exclusive and declared/bound
            // BEFORE the broker is stopped — unlike probe1's exclusive/
            // auto-delete queues (torn down by a restart, so probe1 itself
            // cannot be reused here), a durable, explicitly-bound queue
            // survives a `docker stop`/`docker start` of the SAME container.
            // An earlier version of this test declared the post-outage probe
            // queue only AFTER recovery — that races Wolverine's own outbox
            // redelivery, which is not gated by anything this test controls
            // and can start publishing as soon as it detects the broker is
            // reachable again, fast enough to beat the queue/binding's own
            // AMQP round-trips and (per mandatory=false) silently drop the
            // message. Declaring the queue before the outage begins removes
            // that race entirely: the binding already exists by the time
            // anything is republished.
            //
            // The declaration itself, however, must NOT happen before the
            // outage-phase target user is created: that user's own
            // CreateUser-triggered setup (UserCreated + the initial OPERATOR
            // role's UserRoleAssigned) is a legitimate delivery that happens
            // while the broker is still up. A queue bound before that setup
            // captures it too, so the post-outage drain finds TWO
            // UserRoleAssigned envelopes (the OPERATOR setup grant plus the
            // real HOUSEKEEPER outage-phase grant) instead of one — confirmed
            // by an earlier run of this exact fix. Declaring the queue only
            // after the target user's setup events are confirmed delivered
            // (outboxes empty) and immediately before stopping the broker
            // avoids both races: nothing published before this point can
            // still land in the queue, and nothing published from here on can
            // beat the binding.
            var outageIdentityQueue = $"test-identity-outage-probe-{Guid.NewGuid():N}";
            var outagePmQueue = $"test-pm-outage-probe-{Guid.NewGuid():N}";

            try
            {
                // Target user created WHILE the broker is still up — its own
                // CreateUser-triggered events (UserCreated + the initial OPERATOR
                // role's UserRoleAssigned) must clear BEFORE the outage begins, so
                // only the single, explicit AssignRole below is the tracked
                // Identity event during the outage (confirmed by an earlier run:
                // creating the user AFTER stopping the broker produced two
                // legitimate-but-untracked UserRoleAssigned envelopes — the
                // initial OPERATOR grant plus the explicit HOUSEKEEPER grant —
                // which is correct behavior, not duplication, but broke this
                // test's single-event assumption for the outage phase).
                var assignRoleTargetResponse = await PostAsync(
                    client, "/api/v1/users",
                    new CreateUserRequest("Topology Outage Target", $"{Guid.NewGuid():N}@ihostpro.com", "Correct-Horse-Battery-Staple-99!", "OPERATOR"),
                    token);
                var targetUserId = (await assignRoleTargetResponse.Content.ReadFromJsonAsync<UserResponse>(JsonWebDefaults))!.Id;
                var targetUserSetupDelivered = await WaitUntilAsync(async () => (await CountAllEnvelopesAsync(IdentityOutboxSchema)) == 0, TimeSpan.FromSeconds(15));
                targetUserSetupDelivered.Should().BeTrue("the outage-phase target user's own setup events must clear before the broker goes down");

                // Declared only now — after the target user's own setup events
                // are confirmed delivered — so the queue never observes the
                // initial OPERATOR UserRoleAssigned, only events published
                // during (and redelivered after) the outage below.
                await DeclareDurableOutageProbeQueueAsync(outageIdentityQueue, "identity-events", ["user_logged_in", "user_created", "user_role_assigned"]);
                await DeclareDurableOutageProbeQueueAsync(outagePmQueue, "property-management-events", ["condominium_created"]);

                await _fixture.RabbitMq.StopAsync();

                var assignRoleResponse = await PostAsync(client, $"/api/v1/users/{targetUserId}/roles", new AssignRoleRequest("HOUSEKEEPER"), token);
                assignRoleResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);
                var createCondominium2Response = await PostAsync(client, "/api/v1/condominiums", new CreateCondominiumRequest("Topology Outage Condo", ValidAddress), token);
                createCondominium2Response.StatusCode.Should().Be(HttpStatusCode.Created);
                var outageCondominiumId = (await createCondominium2Response.Content.ReadFromJsonAsync<CondominiumDetailResponse>(JsonWebDefaults))!.Id;

                var identityPendingDuringOutage = await DumpAllEnvelopesAsync(IdentityOutboxSchema);
                var pmPendingDuringOutage = await DumpAllEnvelopesAsync(PropertyManagementOutboxSchema);
                identityPendingDuringOutage.Should().Contain("IHostPro.Contexts.Identity.Contracts.UserRoleAssigned");
                pmPendingDuringOutage.Should().Contain("IHostPro.Contexts.PropertyManagement.Contracts.CondominiumCreated");
                (await CountEnvelopesAsync(PropertyManagementOutboxSchema, "IHostPro.Contexts.Identity.Contracts.UserRoleAssigned")).Should().Be(0);
                (await CountEnvelopesAsync(IdentityOutboxSchema, "IHostPro.Contexts.PropertyManagement.Contracts.CondominiumCreated")).Should().Be(0);

                await _fixture.RabbitMq.StartAsync();

                var readiness2 = Stopwatch.StartNew();
                var ready2 = await WaitForSendingAgentsReadyAsync(runtime, identityDestinations.Concat(pmDestinations), TimeSpan.FromSeconds(30));
                readiness2.Stop();

                var recoveredAfterOutage = await WaitUntilAsync(async () =>
                        (await CountAllEnvelopesAsync(IdentityOutboxSchema)) == 0 &&
                        (await CountAllEnvelopesAsync(PropertyManagementOutboxSchema)) == 0,
                    TimeSpan.FromSeconds(30));

                // No duplication a few seconds after apparent delivery, and isolation still holds.
                await Task.Delay(TimeSpan.FromSeconds(3));
                var finalIdentityCount = await CountAllEnvelopesAsync(IdentityOutboxSchema);
                var finalPmCount = await CountAllEnvelopesAsync(PropertyManagementOutboxSchema);
                var finalPlatformCount = await CountAllEnvelopesAsync(PlatformMessagingSchema);

                // A fresh connection: the one used to declare the durable queues
                // above was opened before the restart and was severed by it — the
                // queues and their bindings, being durable and non-exclusive,
                // survived on the broker; the connection itself did not.
                await using var recoveryConnection = await OpenTestConnectionAsync();
                await using var recoveryChannel = await recoveryConnection.CreateChannelAsync();
                var identityMessages2 = await DrainQueueAsync(recoveryChannel, outageIdentityQueue);
                var pmMessages2 = await DrainQueueAsync(recoveryChannel, outagePmQueue);

                Console.WriteLine(
                    $"Topology check before provisioning: [{string.Join(", ", before.Select(kv => $"{kv.Key}={kv.Value}"))}]. " +
                    $"After first MigrationRunner run: [{string.Join(", ", afterFirstRun.Select(kv => $"{kv.Key}={kv.Value}"))}]. " +
                    $"After second (idempotent) run: [{string.Join(", ", afterSecondRun.Select(kv => $"{kv.Key}={kv.Value}"))}]. " +
                    $"Post-outage sending-agent readiness wait: {readiness2.Elapsed} (ready={ready2}). " +
                    $"Recovered after outage: {recoveredAfterOutage}. " +
                    $"Identity test queue received (post-outage): [{string.Join(", ", identityMessages2.Select(m => $"{m.RoutingKey}/{m.MessageType}"))}]. " +
                    $"PM test queue received (post-outage): [{string.Join(", ", pmMessages2.Select(m => $"{m.RoutingKey}/{m.MessageType}"))}]. " +
                    $"Final counts — identity={finalIdentityCount}, pm={finalPmCount}, platform={finalPlatformCount}.");
                Console.Out.Flush();

                recoveredAfterOutage.Should().BeTrue("with the topology already provisioned and a bound test queue, envelopes published during the outage should be delivered once the broker recovers");
                finalIdentityCount.Should().Be(0);
                finalPmCount.Should().Be(0);
                finalPlatformCount.Should().Be(0);

                var identityOutageMessage = identityMessages2.Should()
                    .ContainSingle(m => m.RoutingKey == "user_role_assigned" && m.MessageType == "IHostPro.Contexts.Identity.Contracts.UserRoleAssigned")
                    .Which;
                var pmOutageMessage = pmMessages2.Should()
                    .ContainSingle(m => m.RoutingKey == "condominium_created" && m.MessageType == "IHostPro.Contexts.PropertyManagement.Contracts.CondominiumCreated")
                    .Which;

                // Count alone would already pass if the queue had (incorrectly)
                // captured the initial OPERATOR grant instead of the outage-phase
                // HOUSEKEEPER one — the payload is what actually distinguishes them.
                var identityOutageEvent = JsonSerializer.Deserialize<UserRoleAssigned>(identityOutageMessage.Body, JsonWebDefaults);
                identityOutageEvent.Should().NotBeNull();
                identityOutageEvent!.AggregateId.Should().Be(targetUserId, "the delivered UserRoleAssigned must belong to the outage-phase target user, not the initial OPERATOR grant");
                identityOutageEvent.RoleCode.Should().Be("HOUSEKEEPER", "the tracked outage-phase assignment was HOUSEKEEPER, not the target user's initial OPERATOR role");

                var pmOutageEvent = JsonSerializer.Deserialize<CondominiumCreated>(pmOutageMessage.Body, JsonWebDefaults);
                pmOutageEvent.Should().NotBeNull();
                pmOutageEvent!.CondominiumId.Should().Be(outageCondominiumId, "the delivered CondominiumCreated must match the condominium created during the outage");
            }
            finally
            {
                await DeleteQueueIfExistsAsync(outageIdentityQueue);
                await DeleteQueueIfExistsAsync(outagePmQueue);
            }
        }
    }

    // ---- Structural regression: each executor selects its own Ancillary Store, never the Main Store ----

    /// <summary>
    /// Real composition root (no mocks) — resolves both transaction
    /// executors from the real Host's DI container and reads
    /// <c>MessageContext.Storage</c> (public getter) directly, confirming
    /// each one's <see cref="IDbContextOutbox{TDbContext}"/> was overridden
    /// to its own Ancillary Store (never <c>IWolverineRuntime.Storage</c>,
    /// the Main store) — the regression test for the root cause fixed via
    /// <c>MessageContext.OverrideStorage</c> in
    /// <c>IdentityOutboxTransactionExecutor</c>/<c>PropertyManagementOutboxTransactionExecutor</c>.
    /// The constructors' own fail-fast check (throws
    /// <see cref="InvalidOperationException"/> if the outbox is not a
    /// <c>MessageContext</c>) is implicitly exercised here too: resolving
    /// either executor at all proves that check did not trip against the
    /// real DI registrations.
    /// </summary>
    [Fact]
    public async Task Each_context_transaction_executor_overrides_storage_to_its_own_ancillary_store_never_the_main_store()
    {
        using var factory = BuildFactory();
        try
        {
            var runtime = factory.Services.GetRequiredService<IWolverineRuntime>();
            var identityStore = runtime.FindAncillaryStoreForMarkerType(typeof(IdentityDbContext));
            var pmStore = runtime.FindAncillaryStoreForMarkerType(typeof(PropertyManagementDbContext));
            var reservationsStore = runtime.FindAncillaryStoreForMarkerType(typeof(ReservationsDbContext));

            using (var identityScope = factory.Services.CreateScope())
            {
                identityScope.ServiceProvider.GetRequiredService<IIdentityTransactionExecutor>(); // triggers OverrideStorage in its constructor
                var identityOutbox = identityScope.ServiceProvider.GetRequiredService<IDbContextOutbox<IdentityDbContext>>();
                var identityMessageContext = identityOutbox.Should().BeAssignableTo<MessageContext>().Subject;

                identityMessageContext.Storage.Should().BeSameAs(identityStore, "Identity's executor must select identity_messaging, never the Main store");
                identityMessageContext.Storage.Should().NotBeSameAs(runtime.Storage, "identity_messaging must never resolve to platform_messaging");
            }

            using (var pmScope = factory.Services.CreateScope())
            {
                pmScope.ServiceProvider.GetRequiredService<IPropertyManagementTransactionExecutor>(); // triggers OverrideStorage in its constructor
                var pmOutbox = pmScope.ServiceProvider.GetRequiredService<IDbContextOutbox<PropertyManagementDbContext>>();
                var pmMessageContext = pmOutbox.Should().BeAssignableTo<MessageContext>().Subject;

                pmMessageContext.Storage.Should().BeSameAs(pmStore, "Property Management's executor must select property_management_messaging, never the Main store");
                pmMessageContext.Storage.Should().NotBeSameAs(runtime.Storage, "property_management_messaging must never resolve to platform_messaging");
            }

            using (var reservationsScope = factory.Services.CreateScope())
            {
                reservationsScope.ServiceProvider.GetRequiredService<IReservationsTransactionExecutor>(); // triggers OverrideStorage in its constructor
                var reservationsOutbox = reservationsScope.ServiceProvider.GetRequiredService<IDbContextOutbox<ReservationsDbContext>>();
                var reservationsMessageContext = reservationsOutbox.Should().BeAssignableTo<MessageContext>().Subject;

                reservationsMessageContext.Storage.Should().BeSameAs(reservationsStore, "Reservations' executor must select reservations_messaging, never the Main store");
                reservationsMessageContext.Storage.Should().NotBeSameAs(runtime.Storage, "reservations_messaging must never resolve to platform_messaging");
                reservationsMessageContext.Storage.Should().NotBeSameAs(identityStore, "reservations_messaging must never resolve to identity_messaging");
                reservationsMessageContext.Storage.Should().NotBeSameAs(pmStore, "reservations_messaging must never resolve to property_management_messaging");
            }
        }
        finally
        {
            ResetEnvironment();
        }
    }

    // ---- Reservations: outage/recovery for one real event (Fase 3, Incremento 1 plan, item 16.9) ----

    /// <summary>
    /// Mirrors <see cref="MigrationRunner_provisions_rabbitmq_topology_idempotently_and_the_real_host_delivers_through_it"/>'s
    /// outage/recovery shape, scoped to a single Reservations event
    /// (<c>ReservationCreated</c>) — proportional coverage per the
    /// Incremento 1 plan: the general outage/recovery mechanism is already
    /// exhaustively proven for Identity/Property Management above; this
    /// confirms Reservations' own <c>reservations_messaging</c> Ancillary
    /// Store and <c>reservation-events</c> exchange participate correctly in
    /// the SAME mechanism, never falling back to the Main store or another
    /// context's schema. Dispatches <see cref="CreateReservationCommand"/>
    /// directly through the real <see cref="IReservationsRequestDispatcher"/>
    /// resolved from the real composition root (no HTTP/JWT needed — the
    /// controller-to-dispatcher wiring is already covered by
    /// <c>ReservationsEndpointsArchitectureTests</c> and the command
    /// handler's own business logic by <c>ReservationCommandHandlerTests</c>).
    ///
    /// Runs entirely inside <see cref="ReservationsOutageVirtualHost"/>, a
    /// RabbitMQ virtual host dedicated to this test on the same shared
    /// broker container (Host configuration, exchange provisioning,
    /// administrative connection, temporary test queue, publish and
    /// consume, and the outage/recovery assertions themselves). RabbitMQ
    /// exchanges are scoped per-vhost, so this test's own
    /// <c>reservation-events</c> exchange never exists in, and can never be
    /// observed from, the default "/" vhost that
    /// <see cref="MigrationRunner_provisions_rabbitmq_topology_idempotently_and_the_real_host_delivers_through_it"/>
    /// asserts against — the two tests share no exchange state and are
    /// independent of execution order (xUnit does not guarantee declaration
    /// order), without narrowing that other test's own coverage of all
    /// three exchanges. This test still uses
    /// <see cref="ProvisionReservationEventsExchangeOnlyAsync"/> rather than
    /// the full <c>IHostPro.MigrationRunner</c> subprocess: the subprocess's
    /// own RabbitMQ wiring targets the default vhost, and re-pointing it at
    /// a dedicated vhost is out of scope for this test-only isolation fix
    /// (no production/tooling code is touched here).
    /// </summary>
    [Fact]
    public async Task Reservations_events_survive_a_rabbitmq_outage_and_deliver_on_recovery_through_the_same_provisioned_topology()
    {
        try
        {
            await ProvisionReservationEventsExchangeOnlyAsync();

            var tenantId = Guid.NewGuid();
            var propertyId = await SeedActivePropertyAsync(tenantId);

            using var factory = BuildFactory(ReservationsOutageVirtualHost);
            var runtime = factory.Services.GetRequiredService<IWolverineRuntime>();

            var reservationDestinations = new[] { new Uri("rabbitmq://exchange/reservation-events/routing/reservation_created") };

            await using var probe = await DeclareTemporaryReservationsTestQueueAsync();

            var ready = await WaitForSendingAgentsReadyAsync(runtime, reservationDestinations, TimeSpan.FromSeconds(30));
            ready.Should().BeTrue("the sending agent for reservation-events/reservation_created must become unlatched within 30s");

            (await CountAllEnvelopesAsync(ReservationsOutboxSchema)).Should().Be(0, "no command has run yet");

            // ---- Outage begins BEFORE the command runs — persistence must not
            // depend on broker reachability. ----
            await _fixture.RabbitMq.StopAsync();

            var reservationId = await DispatchCreateReservationAsync(factory, tenantId, propertyId);

            var pendingDuringOutage = await DumpAllEnvelopesAsync(ReservationsOutboxSchema);
            pendingDuringOutage.Should().Contain("IHostPro.Contexts.Reservations.Contracts.ReservationCreated");
            (await CountAllEnvelopesAsync(PlatformMessagingSchema)).Should().Be(0, "no domain event is ever routed to the Main store");
            (await CountEnvelopesAsync(IdentityOutboxSchema, "IHostPro.Contexts.Reservations.Contracts.ReservationCreated")).Should().Be(0);
            (await CountEnvelopesAsync(PropertyManagementOutboxSchema, "IHostPro.Contexts.Reservations.Contracts.ReservationCreated")).Should().Be(0);

            await _fixture.RabbitMq.StartAsync();

            var recovered = await WaitUntilAsync(
                async () => (await CountAllEnvelopesAsync(ReservationsOutboxSchema)) == 0, TimeSpan.FromSeconds(30));
            recovered.Should().BeTrue("with reservation-events already provisioned and a bound test queue, the envelope published during the outage should be delivered once the broker recovers");

            var messages = await DrainQueueAsync(probe.Channel, probe.Queue);
            messages.Should().ContainSingle(m => m.RoutingKey == "reservation_created" && m.MessageType == "IHostPro.Contexts.Reservations.Contracts.ReservationCreated");

            reservationId.Should().NotBe(Guid.Empty);
        }
        finally
        {
            ResetEnvironment();
        }
    }

    /// <summary>
    /// Declares the <c>reservation-events</c> exchange, inside
    /// <see cref="ReservationsOutageVirtualHost"/>, via Wolverine's own
    /// public <c>DeclareExchange</c>/<c>SetupResources()</c> mechanism — the
    /// SAME public API <see cref="RunMigrationRunnerAsync"/> exercises
    /// indirectly through the real subprocess, but scoped to exactly one
    /// exchange in the dedicated vhost so this test never provisions (and
    /// never depends on) identity-events/property-management-events on the
    /// default vhost. Idempotent — safe to call even if another run already
    /// provisioned it.
    /// </summary>
    private async Task ProvisionReservationEventsExchangeOnlyAsync()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["RabbitMq:Host"] = _fixture.RabbitMq.Hostname,
            ["RabbitMq:VirtualHost"] = ReservationsOutageVirtualHost,
            ["RabbitMq:Username"] = RabbitMqBuilder.DefaultUsername,
            ["RabbitMq:Password"] = RabbitMqBuilder.DefaultPassword,
        }).Build();

        var hostBuilder = Host.CreateApplicationBuilder();
        hostBuilder.UseWolverine(opts =>
            opts.UseIHostProRabbitMq(configuration, listen: false)
                .DeclareExchange("reservation-events", ex => ex.ExchangeType = ExchangeType.Topic));

        using var host = hostBuilder.Build();
        await host.SetupResources();
    }

    private async Task<Guid> SeedActivePropertyAsync(Guid tenantId)
    {
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);

        var address = IHostPro.Contexts.PropertyManagement.Domain.ValueObjects.Address.Create(
            "59090-000", "Rua Exemplo", "100", null, "Ponta Negra", "Natal", "RN", "BR");
        var property = IHostPro.Contexts.PropertyManagement.Domain.Property.Create(
            Guid.NewGuid(), tenantId,
            IHostPro.Contexts.PropertyManagement.Domain.ValueObjects.PropertyCode.Create($"P-{Guid.NewGuid():N}"[..12]),
            "Outage Test Property", 4, condominiumId: null, address, DateTimeOffset.UtcNow);
        property.Activate(DateTimeOffset.UtcNow);

        var options = new DbContextOptionsBuilder<PropertyManagementDbContext>()
            .UseNpgsql(_fixture.AppConnectionString, npgsqlOptions =>
                npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "property_management"))
            .Options;
        await using var dbContext = new PropertyManagementDbContext(options, tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await dbContext.Database.ExecuteSqlInterpolatedAsync($"SELECT set_config('app.tenant_id', {tenantId.ToString()}, true)");

        dbContext.Properties.Add(property);
        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();

        return property.Id;
    }

    private static async Task<Guid> DispatchCreateReservationAsync(WebApplicationFactory<Program> factory, Guid tenantId, Guid propertyId)
    {
        using var scope = factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        sp.GetRequiredService<ITenantContext>().SetTenant(tenantId);

        var command = new CreateReservationCommand(
            tenantId, Guid.NewGuid(), propertyId, "Outage Test Guest", null,
            new DateTimeOffset(2027, 3, 1, 14, 0, 0, TimeSpan.Zero), new DateTimeOffset(2027, 3, 3, 11, 0, 0, TimeSpan.Zero), 2);

        var result = await sp.GetRequiredService<IReservationsRequestDispatcher>().Send(command, CancellationToken.None);
        result.IsSuccess.Should().BeTrue("the durable outbox must persist the command's own write even with the broker unreachable");
        return result.Value.Id;
    }

    private sealed class ReservationsTestQueueProbe : IAsyncDisposable
    {
        public required RabbitMQ.Client.IConnection Connection { get; init; }
        public required RabbitMQ.Client.IChannel Channel { get; init; }
        public required string Queue { get; init; }

        public async ValueTask DisposeAsync()
        {
            await Channel.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }

    private async Task<ReservationsTestQueueProbe> DeclareTemporaryReservationsTestQueueAsync()
    {
        var connectionFactory = new RabbitMQ.Client.ConnectionFactory
        {
            HostName = _fixture.RabbitMq.Hostname,
            UserName = RabbitMqBuilder.DefaultUsername,
            Password = RabbitMqBuilder.DefaultPassword,
            VirtualHost = ReservationsOutageVirtualHost,
        };

        var connection = await connectionFactory.CreateConnectionAsync();
        var channel = await connection.CreateChannelAsync();

        var queue = $"test-reservations-probe-{Guid.NewGuid():N}";
        await channel.QueueDeclareAsync(queue, durable: false, exclusive: true, autoDelete: true);
        await channel.QueueBindAsync(queue, "reservation-events", "reservation_created");

        return new ReservationsTestQueueProbe { Connection = connection, Channel = channel, Queue = queue };
    }

    /// <summary>
    /// Waits for the real, public <see cref="ISendingAgent.Latched"/> signal
    /// on every given destination's sending agent — never a fixed
    /// <c>Task.Delay</c>. <see cref="IEndpointCollection.GetOrBuildSendingAgent"/>
    /// forces agent creation if one does not already exist, so this is safe
    /// to call before any message has ever been sent to that destination.
    /// </summary>
    private static async Task<bool> WaitForSendingAgentsReadyAsync(IWolverineRuntime runtime, IEnumerable<Uri> destinations, TimeSpan timeout)
    {
        var destinationList = destinations.ToArray();
        return await WaitUntilAsync(() => Task.FromResult(
            destinationList.All(uri => !runtime.Endpoints.GetOrBuildSendingAgent(uri, _ => { }).Latched)),
            timeout);
    }

    private sealed record TestQueueMessage(string RoutingKey, string? MessageType, byte[] Body);

    /// <summary>
    /// Test-only diagnostic infrastructure: exclusive, auto-delete queues
    /// bound directly via <c>RabbitMQ.Client</c> (never Wolverine, never
    /// added to any production Host/MigrationRunner) to the two exchanges
    /// MigrationRunner already provisions, so a real publish's success is
    /// actually observable in this test — never persisted, never a
    /// production consumer.
    /// </summary>
    private sealed class TestQueueProbe : IAsyncDisposable
    {
        public required RabbitMQ.Client.IConnection Connection { get; init; }
        public required RabbitMQ.Client.IChannel Channel { get; init; }
        public required string IdentityQueue { get; init; }
        public required string PropertyManagementQueue { get; init; }

        public async ValueTask DisposeAsync()
        {
            await Channel.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }

    private async Task<RabbitMQ.Client.IConnection> OpenTestConnectionAsync()
    {
        var connectionFactory = new RabbitMQ.Client.ConnectionFactory
        {
            HostName = _fixture.RabbitMq.Hostname,
            UserName = RabbitMqBuilder.DefaultUsername,
            Password = RabbitMqBuilder.DefaultPassword,
            VirtualHost = "/",
        };

        return await connectionFactory.CreateConnectionAsync();
    }

    private async Task<TestQueueProbe> DeclareTemporaryTestQueuesAsync()
    {
        var connection = await OpenTestConnectionAsync();
        var channel = await connection.CreateChannelAsync();

        var identityQueue = $"test-identity-probe-{Guid.NewGuid():N}";
        await channel.QueueDeclareAsync(identityQueue, durable: false, exclusive: true, autoDelete: true);
        foreach (var routingKey in new[] { "user_logged_in", "user_created", "user_role_assigned" })
            await channel.QueueBindAsync(identityQueue, "identity-events", routingKey);

        var pmQueue = $"test-pm-probe-{Guid.NewGuid():N}";
        await channel.QueueDeclareAsync(pmQueue, durable: false, exclusive: true, autoDelete: true);
        await channel.QueueBindAsync(pmQueue, "property-management-events", "condominium_created");

        return new TestQueueProbe { Connection = connection, Channel = channel, IdentityQueue = identityQueue, PropertyManagementQueue = pmQueue };
    }

    /// <summary>
    /// Declares a durable, non-exclusive, auto-delete=false queue and binds
    /// it to <paramref name="exchangeName"/> for each of
    /// <paramref name="routingKeys"/> — unlike <see cref="TestQueueProbe"/>
    /// (exclusive+auto-delete, torn down the instant its declaring
    /// connection drops), this queue and its bindings are broker-persisted
    /// state that survive a `docker stop`/`docker start` of the SAME
    /// container. Must be declared BEFORE an outage begins, never after
    /// recovery — see the outage/recovery section of
    /// <see cref="MigrationRunner_provisions_rabbitmq_topology_idempotently_and_the_real_host_delivers_through_it"/>
    /// for the race this avoids. The connection used here is intentionally
    /// short-lived (declare-and-close) since the queue's durability, not the
    /// connection, is what needs to survive the restart.
    /// </summary>
    private async Task DeclareDurableOutageProbeQueueAsync(string queueName, string exchangeName, IEnumerable<string> routingKeys)
    {
        await using var connection = await OpenTestConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();

        await channel.QueueDeclareAsync(queueName, durable: true, exclusive: false, autoDelete: false);
        foreach (var routingKey in routingKeys)
            await channel.QueueBindAsync(queueName, exchangeName, routingKey);
    }

    /// <summary>Best-effort cleanup for a queue declared by <see cref="DeclareDurableOutageProbeQueueAsync"/>.</summary>
    private async Task DeleteQueueIfExistsAsync(string queueName)
    {
        await using var connection = await OpenTestConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();
        await channel.QueueDeleteAsync(queueName, ifUnused: false, ifEmpty: false);
    }

    /// <summary>Reads routing key, AMQP <c>Type</c> property, and the raw message body (for the outage/recovery payload-level assertions only — every other caller ignores <see cref="TestQueueMessage.Body"/>).</summary>
    private static async Task<List<TestQueueMessage>> DrainQueueAsync(RabbitMQ.Client.IChannel channel, string queueName, int maxMessages = 20)
    {
        var results = new List<TestQueueMessage>();
        for (var i = 0; i < maxMessages; i++)
        {
            var result = await channel.BasicGetAsync(queueName, autoAck: true);
            if (result is null) break;
            results.Add(new TestQueueMessage(result.RoutingKey, result.BasicProperties.Type, result.Body.ToArray()));
        }
        return results;
    }

    /// <summary>
    /// Test-only existence check — never used to create anything (no
    /// <c>SetupResources()</c> call here). Uses the exact same public
    /// <see cref="Wolverine.Transports.IBrokerEndpoint.CheckAsync"/> API
    /// <c>BrokerResource.Check()</c> itself calls internally.
    /// </summary>
    private async Task<Dictionary<string, bool>> CheckExchangesExistAsync()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["RabbitMq:Host"] = _fixture.RabbitMq.Hostname,
            ["RabbitMq:VirtualHost"] = "/",
            ["RabbitMq:Username"] = RabbitMqBuilder.DefaultUsername,
            ["RabbitMq:Password"] = RabbitMqBuilder.DefaultPassword,
        }).Build();

        var hostBuilder = Host.CreateApplicationBuilder();
        hostBuilder.UseWolverine(opts =>
        {
            var transport = opts.UseIHostProRabbitMq(configuration, listen: false);
            foreach (var name in MessagingExchangeNames)
                transport.DeclareExchange(name, ex => ex.ExchangeType = ExchangeType.Topic);
        });

        using var host = hostBuilder.Build();
        await host.StartAsync();
        try
        {
            var runtime = host.Services.GetRequiredService<IWolverineRuntime>();
            var results = new Dictionary<string, bool>();
            foreach (var transport in runtime.Options.Transports)
            {
                foreach (var endpoint in transport.Endpoints().OfType<IBrokerEndpoint>())
                {
                    var matchedName = MessagingExchangeNames.FirstOrDefault(n => endpoint.Uri.ToString().Contains(n));
                    if (matchedName is null) continue;
                    results[matchedName] = await endpoint.CheckAsync();
                }
            }
            return results;
        }
        finally
        {
            await host.StopAsync();
        }
    }

    private static string FindSolutionRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "IHostPro.sln")))
            dir = dir.Parent;

        if (dir is null)
            throw new InvalidOperationException($"Could not locate IHostPro.sln walking up from {AppContext.BaseDirectory}.");

        return dir.FullName;
    }

    /// <summary>
    /// Runs the actual built <c>IHostPro.MigrationRunner</c> executable as a
    /// real subprocess (never re-implemented in-test) against the fixture's
    /// Postgres (migrator role) and RabbitMQ containers via environment
    /// variables — the same override mechanism <see cref="BuildFactory"/>
    /// already uses for the Api.
    /// </summary>
    private async Task<(int ExitCode, string Output)> RunMigrationRunnerAsync()
    {
        var dllPath = Path.Combine(FindSolutionRoot(), "tools", "IHostPro.MigrationRunner", "bin", "Release", "net10.0", "IHostPro.MigrationRunner.dll");
        if (!File.Exists(dllPath))
            throw new InvalidOperationException($"MigrationRunner build output not found at {dllPath}. Build IHostPro.MigrationRunner in Release configuration first.");

        var psi = new ProcessStartInfo("dotnet", $"\"{dllPath}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        psi.Environment["DOTNET_ENVIRONMENT"] = "Development";
        psi.Environment["ConnectionStrings__Identity"] = _fixture.MigratorConnectionString;
        psi.Environment["ConnectionStrings__PropertyManagement"] = _fixture.MigratorConnectionString;
        psi.Environment["ConnectionStrings__Reservations"] = _fixture.MigratorConnectionString;
        psi.Environment["ConnectionStrings__Configuration"] = _fixture.MigratorConnectionString;
        psi.Environment["ConnectionStrings__Housekeeping"] = _fixture.MigratorConnectionString;
        psi.Environment["ConnectionStrings__Communication"] = _fixture.MigratorConnectionString;
        psi.Environment["ConnectionStrings__Dashboard"] = _fixture.MigratorConnectionString;
        psi.Environment["ConnectionStrings__Platform"] = _fixture.MigratorConnectionString;
        psi.Environment["RabbitMq__Host"] = _fixture.RabbitMq.Hostname;
        psi.Environment["RabbitMq__VirtualHost"] = "/";
        psi.Environment["RabbitMq__Username"] = RabbitMqBuilder.DefaultUsername;
        psi.Environment["RabbitMq__Password"] = RabbitMqBuilder.DefaultPassword;

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start MigrationRunner process.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await stdoutTask + await stderrTask;

        return (process.ExitCode, output);
    }

    private static async Task<bool> WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
                return true;
            await Task.Delay(TimeSpan.FromMilliseconds(250));
        }
        return await condition();
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string route, object body, string? token) =>
        await SendAsync(client, HttpMethod.Post, route, body, token);

    private static async Task<HttpResponseMessage> GetAsync(HttpClient client, string route, string? token) =>
        await SendAsync(client, HttpMethod.Get, route, null, token);

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string route, object? body, string? token)
    {
        var request = new HttpRequestMessage(method, route);
        if (body is not null)
            request.Content = JsonContent.Create(body, options: JsonWebDefaults);
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return await client.SendAsync(request);
    }

    private async Task<long> CountEnvelopesAsync(string schema, string messageType)
    {
        await using var connection = new NpgsqlConnection(_fixture.MigratorConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT count(*) FROM {schema}.wolverine_outgoing_envelopes WHERE message_type = @messageType";
        command.Parameters.AddWithValue("messageType", messageType);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task<List<string>> DumpAllEnvelopesAsync(string schema)
    {
        await using var connection = new NpgsqlConnection(_fixture.MigratorConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT message_type FROM {schema}.wolverine_outgoing_envelopes";
        await using var reader = await command.ExecuteReaderAsync();
        var result = new List<string>();
        while (await reader.ReadAsync())
            result.Add(reader.GetString(0));
        return result;
    }

    private async Task<long> CountAllEnvelopesAsync(string schema)
    {
        await using var connection = new NpgsqlConnection(_fixture.MigratorConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT count(*) FROM {schema}.wolverine_outgoing_envelopes";
        return (long)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>
    /// Minimal envelope content — <c>message_type</c> and <c>destination</c>
    /// only, never <c>body</c> (which carries the serialized event, including
    /// tenant/actor identifiers) — enough to confirm routing without
    /// exposing payload data in test output.
    /// </summary>
    private async Task<List<EnvelopeMetadata>> DumpEnvelopeMetadataAsync(string schema, string messageType)
    {
        await using var connection = new NpgsqlConnection(_fixture.MigratorConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT message_type, destination FROM {schema}.wolverine_outgoing_envelopes WHERE message_type = @messageType";
        command.Parameters.AddWithValue("messageType", messageType);
        await using var reader = await command.ExecuteReaderAsync();
        var result = new List<EnvelopeMetadata>();
        while (await reader.ReadAsync())
            result.Add(new EnvelopeMetadata(reader.GetString(0), reader.GetString(1)));
        return result;
    }

    private sealed record EnvelopeMetadata(string MessageType, string Destination);

    /// <summary>
    /// Full envelope metadata for recovery diagnostics — <c>id</c>,
    /// <c>owner_id</c>, <c>destination</c>, <c>deliver_by</c>, <c>attempts</c>,
    /// <c>message_type</c> only, never <c>body</c>.
    /// </summary>
    private sealed record EnvelopeFullMetadata(Guid Id, int OwnerId, string Destination, DateTimeOffset? DeliverBy, int Attempts, string MessageType);

    private async Task<List<EnvelopeFullMetadata>> DumpEnvelopeFullMetadataAsync(string schema)
    {
        await using var connection = new NpgsqlConnection(_fixture.MigratorConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT id, owner_id, destination, deliver_by, attempts, message_type FROM {schema}.wolverine_outgoing_envelopes";
        await using var reader = await command.ExecuteReaderAsync();
        var result = new List<EnvelopeFullMetadata>();
        while (await reader.ReadAsync())
            result.Add(new EnvelopeFullMetadata(
                reader.GetGuid(0),
                reader.GetInt32(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3),
                reader.GetInt32(4),
                reader.GetString(5)));
        return result;
    }

    private static async Task<long> CountRowsAsync(string connectionString, string schema, string table)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT count(*) FROM {schema}.{table}";
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
