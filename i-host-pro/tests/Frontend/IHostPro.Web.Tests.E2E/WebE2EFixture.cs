using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using IHostPro.BuildingBlocks.Infrastructure.Messaging;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.Contexts.Identity.Domain;
using IHostPro.Contexts.Identity.Domain.ValueObjects;
using IHostPro.Contexts.Identity.Infrastructure.Persistence;
using IHostPro.Contexts.Identity.Infrastructure.Security;
using JasperFx;
using JasperFx.Resources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;
using Npgsql;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;
using Testcontainers.Redis;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.Postgresql;
using Wolverine.RabbitMQ;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace IHostPro.Web.Tests.E2E;

/// <summary>
/// Boots the REAL, unmodified <c>IHostPro.Api</c> and <c>IHostPro.Web</c>
/// (Angular) as two real subprocesses — never <see cref="Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory{TEntryPoint}"/>'s
/// in-process TestServer, which a real Playwright-driven browser cannot
/// reach over the network — against real, ephemeral PostgreSQL/RabbitMQ
/// containers, exactly mirroring <c>IHostPro.MigrationRunner</c>'s own
/// schema/topology provisioning (Fase 2 Checkpoint 6 homologação) and
/// <c>WolverineThreeStoreCompositionTests.Fixture</c>'s seeding pattern
/// (Fase 4, Checkpoint 4 Playwright E2E). No component of this suite is
/// mocked or hand-reproduced: the same compiled binaries a real deployment
/// runs are the ones a real Chromium instance drives here.
///
/// Both the API (5140) and the frontend dev server (4200) are bound to the
/// SAME fixed ports the committed <c>frontend/IHostPro.Web/public/config.json</c>
/// and <c>appsettings.json</c> CORS policy already assume — like RabbitMQ's
/// own fixed port 5672 (Wolverine's RabbitMQ transport wiring has no port
/// override, see <c>WolverineThreeStoreCompositionTests</c>), this means
/// this suite cannot run concurrently with another instance of itself, a
/// manually-started dev server, or <c>WolverineThreeStoreCompositionTests</c>
/// (same RabbitMQ port constraint) — an already-accepted constraint in this
/// codebase, not new here.
/// </summary>
public sealed class WebE2EFixture : IAsyncLifetime
{
    private const string AppRolePassword = "test_app_password";
    private const string MigratorRolePassword = "test_migrator_password";
    private const int ApiPort = 5140;
    private const int WebPort = 4200;

    public const string TenantSlugValue = "e2e-playwright";
    public const string AdminEmail = "admin@e2e-playwright.test";
    public const string AdminPassword = "Correct-Horse-Battery-Staple-77!";
    public const string AdminFullName = "E2E Playwright Admin";

    /// <summary>
    /// OPERATOR — a real seeded role that, per <c>IdentityCatalogSeed</c>, is
    /// never granted <c>USERS:MANAGE</c>. Used exclusively by the
    /// authorization-focused tests (Fase 4, Incremento 2) to prove the
    /// "Usuários" nav item and the <c>/users</c> route are gated on the
    /// user's real effective permissions, never on being merely authenticated.
    /// </summary>
    public const string OperatorEmail = "operator@e2e-playwright.test";
    public const string OperatorPassword = "Correct-Horse-Battery-Staple-88!";
    public const string OperatorFullName = "E2E Playwright Operator";

    /// <summary>
    /// Checkpoint 7 homologação (Fase 5), real conflict found and resolved by
    /// explicit user decision: <c>IdentityCatalogSeed</c> deliberately gives
    /// no single role both <c>POLICIES:READ</c> and <c>POLICIES:MANAGE</c>
    /// (only ADMIN has MANAGE, only AI_AGENT has READ) — confirmed by direct
    /// HTTP observation against a real running API that an ADMIN-only token
    /// gets <c>403</c> on every read endpoint <c>PoliciesController</c>
    /// exposes (<c>List</c>/<c>GetValue</c>/<c>GetEffective</c>/<c>GetHistory</c>),
    /// which breaks the natural single-screen admin workflow
    /// <c>PoliciesE2ETests</c> exercises (open a dialog, see the current
    /// value, write a new one, see it reflected). This persona holds BOTH
    /// <c>ADMIN</c> and <c>AI_AGENT</c> (see <see cref="SeedTenantAndAdminAsync"/>)
    /// — a test-fixture-only combination, never touching the approved
    /// production permission catalog — used exclusively by
    /// <c>PoliciesE2ETests</c>. <see cref="PoliciesAuthorizationE2ETests"/>
    /// keeps using the standard <see cref="AdminEmail"/>/<see cref="OperatorEmail"/>,
    /// since it only asserts route/nav access (OR semantics on either
    /// permission), never a data read.
    /// </summary>
    public const string PolicyAdminEmail = "policy-admin@e2e-playwright.test";
    public const string PolicyAdminPassword = "Correct-Horse-Battery-Staple-99!";
    public const string PolicyAdminFullName = "E2E Playwright Policy Admin";

    public string ApiBaseUrl { get; } = $"http://localhost:{ApiPort}";
    public string WebBaseUrl { get; } = $"http://localhost:{WebPort}";

    private static readonly TimeSpan ProcessStopTimeout = TimeSpan.FromSeconds(15);

    private PostgreSqlContainer _postgresContainer = null!;
    private RabbitMqContainer _rabbitMqContainer = null!;
    private RedisContainer _redisContainer = null!;
    private string _migratorConnectionString = null!;
    private string _appConnectionString = null!;
    private Guid _tenantId;
    private ManagedProcess? _apiProcess;
    private ManagedProcess? _webProcess;
    private ManagedProcess? _workerProcess;
    private IPlaywright? _playwright;
    private int _cleanedUp;
    public IBrowser Browser { get; private set; } = null!;

    /// <summary>
    /// If any step throws — a container failing to start, a migration
    /// failing, the API/Angular process never becoming ready, the browser
    /// failing to launch — <see cref="CleanupAsync"/> runs before the
    /// exception propagates. This is required, not optional: xUnit does not
    /// reliably call <see cref="DisposeAsync"/> when <see cref="IAsyncLifetime.InitializeAsync"/>
    /// itself throws, so without this catch, whatever had already started
    /// (containers, the API process, the Angular process) would leak for the
    /// lifetime of the test host. The original exception is always what
    /// propagates — cleanup failures are logged, never substituted in its
    /// place.
    /// </summary>
    public async Task InitializeAsync()
    {
        try
        {
            await StartPostgresAsync();
            await StartRabbitMqAsync();
            await StartRedisAsync();
            await MigrateSchemasAsync();
            await ProvisionMessageStoresAsync();
            await ProvisionRabbitMqTopologyAsync();
            await SeedTenantAndAdminAsync();

            _apiProcess = StartApiProcess();
            await WaitForHttpReadyAsync(ApiBaseUrl + "/swagger/v1/swagger.json", TimeSpan.FromSeconds(60));

            // Checkpoint 7 homologação (Fase 5), real gap found and fixed: without a
            // real IHostPro.Worker running, nothing ever consumes PolicyUpdated, so
            // the Redis-backed effective-policy cache (Checkpoint 6) is never
            // invalidated after a write — confirmed by direct observation, a UI flow
            // that writes a new policy version and then re-reads its effective
            // resolution (exactly what PolicyDetailDialog.submitNewVersion does)
            // sees a stale cached resolution. Mirrors StartApiProcess's own
            // RabbitMq/Redis wiring so both processes share the same physical
            // broker/cache the real deployment does.
            _workerProcess = StartWorkerProcess();

            _webProcess = StartWebProcess();
            await WaitForHttpReadyAsync(WebBaseUrl, TimeSpan.FromSeconds(90));

            Microsoft.Playwright.Program.Main(["install", "chromium"]);
            _playwright = await Playwright.CreateAsync();
            Browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        }
        catch
        {
            var diagnostics = await CleanupAsync();
            if (diagnostics.Count > 0)
                await Console.Error.WriteLineAsync("WebE2EFixture.InitializeAsync failed; cleanup ran and reported: " + string.Join(" | ", diagnostics));
            throw;
        }
    }

    /// <summary>Delegates to the same idempotent <see cref="CleanupAsync"/> InitializeAsync's own failure path uses. A leaked resource here is never swallowed — it fails this call loudly (an orphaned process/container is exactly the defect class this hardening exists to catch), unlike InitializeAsync's path, which must let the original startup exception win instead.</summary>
    public async Task DisposeAsync()
    {
        var diagnostics = await CleanupAsync();
        if (diagnostics.Count > 0)
            throw new InvalidOperationException("WebE2EFixture cleanup left orphaned resources: " + string.Join(" | ", diagnostics));
    }

    /// <summary>
    /// The single teardown routine, safe to call more than once (a second
    /// call is a no-op — guarded by <see cref="_cleanedUp"/>) and safe to
    /// call when only part of the infrastructure was ever created (every
    /// step is null-checked). Every step always runs, regardless of an
    /// earlier step's outcome, so one broken container disposal can never
    /// prevent the browser or a process from being torn down — each
    /// failure is collected into the returned list instead of thrown
    /// mid-sequence. Ordered shutdown: browser, then Playwright itself,
    /// then Angular, then the API (reverse of startup order), then the
    /// ephemeral containers.
    /// </summary>
    private async Task<IReadOnlyList<string>> CleanupAsync()
    {
        if (Interlocked.Exchange(ref _cleanedUp, 1) != 0)
            return [];

        var diagnostics = new List<string>();

        if (Browser is not null)
        {
            try { await Browser.CloseAsync(); }
            catch (Exception ex) { diagnostics.Add($"Browser.CloseAsync: {ex.Message}"); }
        }
        try { _playwright?.Dispose(); }
        catch (Exception ex) { diagnostics.Add($"Playwright.Dispose: {ex.Message}"); }

        if (_webProcess is not null)
        {
            var diagnostic = await _webProcess.StopAsync(ProcessStopTimeout);
            if (diagnostic is not null)
                diagnostics.Add(diagnostic);
            else if (IsPortInUse(WebPort))
                diagnostics.Add($"Port {WebPort} is still in use after the Angular dev server process reported exited — a different, untracked process is now holding it.");
        }
        if (_apiProcess is not null)
        {
            var diagnostic = await _apiProcess.StopAsync(ProcessStopTimeout);
            if (diagnostic is not null)
                diagnostics.Add(diagnostic);
            else if (IsPortInUse(ApiPort))
                diagnostics.Add($"Port {ApiPort} is still in use after the API process reported exited — a different, untracked process is now holding it.");
        }
        if (_workerProcess is not null)
        {
            var diagnostic = await _workerProcess.StopAsync(ProcessStopTimeout);
            if (diagnostic is not null)
                diagnostics.Add(diagnostic);
        }

        if (_rabbitMqContainer is not null)
        {
            try { await _rabbitMqContainer.DisposeAsync(); }
            catch (Exception ex) { diagnostics.Add($"RabbitMQ container disposal: {ex.Message}"); }
        }
        if (_redisContainer is not null)
        {
            try { await _redisContainer.DisposeAsync(); }
            catch (Exception ex) { diagnostics.Add($"Redis container disposal: {ex.Message}"); }
        }
        if (_postgresContainer is not null)
        {
            try { await _postgresContainer.DisposeAsync(); }
            catch (Exception ex) { diagnostics.Add($"Postgres container disposal: {ex.Message}"); }
        }

        return diagnostics;
    }

    private static bool IsPortInUse(int port) =>
        IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(endpoint => endpoint.Port == port);

    private async Task StartPostgresAsync()
    {
        _postgresContainer = new PostgreSqlBuilder()
            .WithImage("postgres:16")
            .WithDatabase("ihostpro_e2e")
            .WithUsername("ihostpro")
            .WithPassword("ihostpro_dev")
            .Build();
        await _postgresContainer.StartAsync();

        var adminConnectionString = _postgresContainer.GetConnectionString();
        await using (var adminConnection = new NpgsqlConnection(adminConnectionString))
        {
            // Testcontainers' readiness check runs `pg_isready` *inside* the container (over
            // Docker's own socket), which can report ready a moment before the host↔container
            // port publish (Docker Desktop/WSL2 NAT) has actually finished converging — a real,
            // observed race, not a fixture bug. A short bounded retry bridges that gap without
            // masking a genuinely unreachable database.
            await OpenWithRetryAsync(adminConnection);
            await using var command = adminConnection.CreateCommand();
            command.CommandText = $"""
                CREATE ROLE ihostpro_migrator LOGIN PASSWORD '{MigratorRolePassword}';
                CREATE ROLE ihostpro_app LOGIN PASSWORD '{AppRolePassword}';
                GRANT CREATE ON DATABASE ihostpro_e2e TO ihostpro_migrator;
                """;
            await command.ExecuteNonQueryAsync();
        }

        var builder = new NpgsqlConnectionStringBuilder(adminConnectionString) { Username = "ihostpro_migrator", Password = MigratorRolePassword };
        _migratorConnectionString = builder.ConnectionString;
        builder.Username = "ihostpro_app";
        builder.Password = AppRolePassword;
        _appConnectionString = builder.ConnectionString;
    }

    /// <summary>
    /// Fixed host port 5672 — Wolverine's RabbitMQ transport wiring
    /// (<c>WolverineConfigurationExtensions.UseIHostProRabbitMq</c>) has no
    /// port override, exactly like <c>WolverineThreeStoreCompositionTests</c>
    /// already documents. Caller is responsible for ensuring nothing else on
    /// the host (a manually-started dev server, the homolog stack, another
    /// test run) currently owns that port.
    /// </summary>
    private async Task StartRabbitMqAsync()
    {
        _rabbitMqContainer = new RabbitMqBuilder()
            .WithImage("rabbitmq:3-management-alpine")
            .WithPortBinding(5672, 5672)
            .Build();
        await _rabbitMqContainer.StartAsync();
    }

    private async Task StartRedisAsync()
    {
        _redisContainer = new RedisBuilder().WithImage("redis:7-alpine").Build();
        await _redisContainer.StartAsync();
    }

    private async Task MigrateSchemasAsync()
    {
        await using (var identityDbContext = CreateIdentityDbContext())
            await identityDbContext.Database.MigrateAsync();
        await using (var pmDbContext = CreatePropertyManagementDbContext())
            await pmDbContext.Database.MigrateAsync();
        await using (var reservationsDbContext = CreateReservationsDbContext())
            await reservationsDbContext.Database.MigrateAsync();
        await using (var configurationDbContext = CreateConfigurationDbContext())
            await configurationDbContext.Database.MigrateAsync();
        // Fase 6, Checkpoint 6 homologação: was missing entirely — see this
        // file's own StartWorkerProcess/ProvisionRabbitMqTopologyAsync doc
        // comments for the full real-failure narrative this gap caused.
        await using (var housekeepingDbContext = CreateHousekeepingDbContext())
            await housekeepingDbContext.Database.MigrateAsync();
    }

    /// <summary>Mirrors IHostPro.MigrationRunner exactly: platform_messaging (Main) first, then the five Ancillary outboxes.</summary>
    private async Task ProvisionMessageStoresAsync()
    {
        await ProvisionMessageStoreSchemaAsync("platform_messaging", dbContextType: null);
        await ProvisionMessageStoreSchemaAsync("identity_messaging", typeof(IdentityDbContext));
        await ProvisionMessageStoreSchemaAsync("property_management_messaging", typeof(IHostPro.Contexts.PropertyManagement.Infrastructure.Persistence.PropertyManagementDbContext));
        await ProvisionMessageStoreSchemaAsync("reservations_messaging", typeof(IHostPro.Contexts.Reservations.Infrastructure.Persistence.ReservationsDbContext));
        await ProvisionMessageStoreSchemaAsync("configuration_messaging", typeof(IHostPro.Contexts.Configuration.Infrastructure.Persistence.ConfigurationDbContext));
        await ProvisionMessageStoreSchemaAsync("housekeeping_messaging", typeof(IHostPro.Contexts.Housekeeping.Infrastructure.Persistence.HousekeepingDbContext));
    }

    private async Task ProvisionMessageStoreSchemaAsync(string schema, Type? dbContextType)
    {
        var hostBuilder = Host.CreateApplicationBuilder();
        hostBuilder.UseWolverine(opts =>
        {
            if (dbContextType is null)
                opts.PersistMessagesWithPostgresql(_migratorConnectionString, schema);
            else
                opts.EnrollAncillaryPostgresqlOutbox(_migratorConnectionString, schema, dbContextType);
            opts.AutoBuildMessageStorageOnStartup = AutoCreate.None;
            opts.UseEntityFrameworkCoreTransactions();
        });

        using var setupHost = hostBuilder.Build();
        await setupHost.SetupResources();

        await using var connection = new NpgsqlConnection(_migratorConnectionString);
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

    /// <summary>Mirrors IHostPro.MigrationRunner's exchange declaration exactly — the same shared connection extension, never a raw RabbitMQ.Client call.</summary>
    private async Task ProvisionRabbitMqTopologyAsync()
    {
        var rabbitConfiguration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["RabbitMq:Host"] = _rabbitMqContainer.Hostname,
            ["RabbitMq:VirtualHost"] = "/",
            ["RabbitMq:Username"] = RabbitMqBuilder.DefaultUsername,
            ["RabbitMq:Password"] = RabbitMqBuilder.DefaultPassword,
        }).Build();

        var topologyHostBuilder = Host.CreateApplicationBuilder();
        topologyHostBuilder.UseWolverine(opts =>
        {
            opts.UseIHostProRabbitMq(rabbitConfiguration, listen: false)
                .DeclareExchange("identity-events", exchange => exchange.ExchangeType = ExchangeType.Topic)
                // Fase 6, Checkpoint 6 homologação: this fixture's own topology
                // provisioning predates Housekeeping and never learned about its
                // two consumer queues — the identical class of gap already found
                // and fixed once in this same checkpoint for
                // PolicyUpdatedWolverineDiscoveryTests.cs's own hand-rolled
                // topology (see the Fase 6 homologation doc, §10.9). Confirmed by
                // a real failing run: the real Worker subprocess this fixture
                // starts (StartWorkerProcess) now unconditionally calls
                // ListenToRabbitQueue for housekeeping.property-projection/
                // housekeeping.reservation-projection too (Worker hosts every
                // Bounded Context's consumers in one process since ADR-015), so
                // it crashed at startup with a real AMQP 404
                // ("no queue 'housekeeping.property-projection' in vhost '/'")
                // before ever reaching readiness — which in turn left the real
                // IHostPro.Api process never started at all (this fixture only
                // proceeds to start Api after the Worker signals it is
                // listening), surfacing here as every single E2E test timing out
                // waiting for a completely unreachable Api. Mirrors
                // IHostPro.MigrationRunner's own declarations exactly.
                .DeclareExchange("property-management-events", exchange =>
                {
                    exchange.ExchangeType = ExchangeType.Topic;
                    exchange.BindQueue("housekeeping.property-projection", "property_created");
                    exchange.BindQueue("housekeeping.property-projection", "property_activated");
                    exchange.BindQueue("housekeeping.property-projection", "property_deactivated");
                    exchange.BindQueue("housekeeping.property-projection", "property_archived");
                })
                .DeclareExchange("reservation-events", exchange =>
                {
                    exchange.ExchangeType = ExchangeType.Topic;
                    exchange.BindQueue("housekeeping.reservation-projection", "reservation_created");
                    exchange.BindQueue("housekeeping.reservation-projection", "reservation_cancelled");
                })
                // Fase 7, Incremento 1, Checkpoint 3: was declared with NO
                // queue bound at all — the real Worker subprocess this
                // fixture starts calls opts.ListenToRabbitQueue on
                // "reservations.cleaning-schedule-projection" (Reservations'
                // own CleaningScheduleProjection consumer, added Fase 7
                // Checkpoint 1), which crashed the Worker at startup with
                // AMQP 404 NOT_FOUND the instant it tried to listen on a
                // queue that had never been declared — found the same way
                // as the ConnectionStrings__Reservations gap above (a real
                // ScheduleAgendaE2ETests run, real crash log). Binds all ten
                // real Cleaning lifecycle routing keys, mirroring
                // IHostPro.MigrationRunner's own declaration exactly
                // (cleaning_delayed deliberately excluded — see
                // MigrationRunner's own comment, Documento 07 §29.8).
                .DeclareExchange("housekeeping-events", exchange =>
                {
                    exchange.ExchangeType = ExchangeType.Topic;
                    exchange.BindQueue("reservations.cleaning-schedule-projection", "cleaning_created");
                    exchange.BindQueue("reservations.cleaning-schedule-projection", "cleaning_assigned");
                    exchange.BindQueue("reservations.cleaning-schedule-projection", "cleaning_in_transit");
                    exchange.BindQueue("reservations.cleaning-schedule-projection", "cleaning_started");
                    exchange.BindQueue("reservations.cleaning-schedule-projection", "cleaning_inspection_started");
                    exchange.BindQueue("reservations.cleaning-schedule-projection", "cleaning_completed");
                    exchange.BindQueue("reservations.cleaning-schedule-projection", "cleaning_interrupted");
                    exchange.BindQueue("reservations.cleaning-schedule-projection", "cleaning_needs_help");
                    exchange.BindQueue("reservations.cleaning-schedule-projection", "cleaning_needs_material");
                    exchange.BindQueue("reservations.cleaning-schedule-projection", "cleaning_cancelled");
                })
                // Fase 5, Checkpoint 7 homologação: was missing entirely — the
                // same "configuration-events" gap already found and fixed in
                // IHostPro.MigrationRunner (see the Fase 5 homologation doc,
                // §13.7). Also declares the "configuration.policy-updated" queue
                // and its binding, mirroring MigrationRunner exactly, since this
                // fixture now runs a real IHostPro.Worker (StartWorkerProcess)
                // that calls opts.ListenToRabbitQueue("configuration.policy-updated")
                // expecting it to already exist — never provisioned by a host at
                // runtime, same single-provisioning-authority pattern as
                // production.
                .DeclareExchange("configuration-events", exchange =>
                {
                    exchange.ExchangeType = ExchangeType.Topic;
                    exchange.BindQueue("configuration.policy-updated", "policy_updated");
                });
        });

        using var topologyHost = topologyHostBuilder.Build();
        await topologyHost.SetupResources();

        var runtime = topologyHost.Services.GetRequiredService<IWolverineRuntime>();
        foreach (var transport in runtime.Options.Transports)
        foreach (var endpoint in transport.Endpoints().OfType<IBrokerEndpoint>())
        {
            if (!await endpoint.CheckAsync())
                throw new InvalidOperationException($"RabbitMQ topology provisioning failed: endpoint '{endpoint.Uri}' does not exist after SetupResources().");
        }
    }

    private async Task SeedTenantAndAdminAsync()
    {
        var tenantId = Guid.NewGuid();
        _tenantId = tenantId;
        await using var dbContext = CreateIdentityDbContext(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await dbContext.Database.ExecuteSqlInterpolatedAsync($"SELECT set_config('app.tenant_id', {tenantId.ToString()}, true)");

        var tenant = Tenant.Provision(tenantId, TenantSlug.Create(TenantSlugValue), "E2E Playwright Tenant", DateTimeOffset.UtcNow);
        dbContext.Tenants.Add(tenant);

        var hasher = new Argon2PasswordHasher(new KonsciousArgon2idPrimitive(), Options.Create(new Argon2Options()));
        var now = DateTimeOffset.UtcNow;

        var adminHash = PasswordHash.FromEncoded(hasher.HashPassword(null!, AdminPassword));
        var admin = User.Register(Guid.NewGuid(), tenantId, Email.Create(AdminEmail), AdminFullName, adminHash, now);
        dbContext.Users.Add(admin);
        dbContext.UserRoles.Add(new UserRole(tenantId, admin.Id, "ADMIN", now, assignedByUserId: null));

        var operatorHash = PasswordHash.FromEncoded(hasher.HashPassword(null!, OperatorPassword));
        var operatorUser = User.Register(Guid.NewGuid(), tenantId, Email.Create(OperatorEmail), OperatorFullName, operatorHash, now);
        dbContext.Users.Add(operatorUser);
        dbContext.UserRoles.Add(new UserRole(tenantId, operatorUser.Id, "OPERATOR", now, assignedByUserId: null));

        // See PolicyAdminEmail's own doc comment: both roles are needed only
        // because no single role in the approved catalog holds both
        // POLICIES:READ and POLICIES:MANAGE.
        var policyAdminHash = PasswordHash.FromEncoded(hasher.HashPassword(null!, PolicyAdminPassword));
        var policyAdmin = User.Register(Guid.NewGuid(), tenantId, Email.Create(PolicyAdminEmail), PolicyAdminFullName, policyAdminHash, now);
        dbContext.Users.Add(policyAdmin);
        dbContext.UserRoles.Add(new UserRole(tenantId, policyAdmin.Id, "ADMIN", now, assignedByUserId: null));
        dbContext.UserRoles.Add(new UserRole(tenantId, policyAdmin.Id, "AI_AGENT", now, assignedByUserId: null));

        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();
    }

    /// <summary>
    /// Provisions a SECOND, fully independent tenant + ADMIN directly via EF
    /// Core, mirroring <see cref="SeedTenantAndAdminAsync"/> exactly — for the
    /// one real-browser scenario that genuinely needs two tenants in the same
    /// running system (proving one tenant's Agenda never shows another
    /// tenant's data). No other E2E suite has needed this: every other
    /// cross-tenant/RLS assertion in this codebase is already covered by real
    /// integration tests reading under a different tenant context, never by
    /// driving a second browser session — this is the first Playwright
    /// scenario for which that substitution would not be equivalent (the
    /// Agenda's own frontend query/render path only runs in a real browser).
    /// Each call provisions a brand-new tenant, safe to call more than once
    /// per test run.
    /// </summary>
    public async Task<(Guid TenantId, string TenantSlugValue, string AdminEmail, string AdminPassword)> CreateAdditionalTenantWithAdminAsync()
    {
        var tenantId = Guid.NewGuid();
        var slugValue = $"e2e-second-{tenantId:N}"[..24];
        var adminEmail = $"admin-{tenantId:N}@e2e-second.test";
        const string adminPassword = "Correct-Horse-Battery-Staple-55!";

        await using var dbContext = CreateIdentityDbContext(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await dbContext.Database.ExecuteSqlInterpolatedAsync($"SELECT set_config('app.tenant_id', {tenantId.ToString()}, true)");

        var tenant = Tenant.Provision(tenantId, TenantSlug.Create(slugValue), "E2E Second Tenant", DateTimeOffset.UtcNow);
        dbContext.Tenants.Add(tenant);

        var hasher = new Argon2PasswordHasher(new KonsciousArgon2idPrimitive(), Options.Create(new Argon2Options()));
        var now = DateTimeOffset.UtcNow;
        var adminHash = PasswordHash.FromEncoded(hasher.HashPassword(null!, adminPassword));
        var admin = User.Register(Guid.NewGuid(), tenantId, Email.Create(adminEmail), "E2E Second Tenant Admin", adminHash, now);
        dbContext.Users.Add(admin);
        dbContext.UserRoles.Add(new UserRole(tenantId, admin.Id, "ADMIN", now, assignedByUserId: null));

        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();

        return (tenantId, slugValue, adminEmail, adminPassword);
    }

    private IdentityDbContext CreateIdentityDbContext(Guid? tenantId = null)
    {
        var tenantContext = new TenantContext();
        if (tenantId is { } id)
            tenantContext.SetTenant(id);
        var options = new DbContextOptionsBuilder<IdentityDbContext>()
            .UseNpgsql(tenantId is null ? _migratorConnectionString : _appConnectionString, npgsqlOptions =>
                npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "identity"))
            .Options;
        return new IdentityDbContext(options, tenantContext);
    }

    private IHostPro.Contexts.PropertyManagement.Infrastructure.Persistence.PropertyManagementDbContext CreatePropertyManagementDbContext()
    {
        var options = new DbContextOptionsBuilder<IHostPro.Contexts.PropertyManagement.Infrastructure.Persistence.PropertyManagementDbContext>()
            .UseNpgsql(_migratorConnectionString, npgsqlOptions =>
                npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "property_management"))
            .Options;
        return new IHostPro.Contexts.PropertyManagement.Infrastructure.Persistence.PropertyManagementDbContext(options, new TenantContext());
    }

    private IHostPro.Contexts.Configuration.Infrastructure.Persistence.ConfigurationDbContext CreateConfigurationDbContext()
    {
        var options = new DbContextOptionsBuilder<IHostPro.Contexts.Configuration.Infrastructure.Persistence.ConfigurationDbContext>()
            .UseNpgsql(_migratorConnectionString, npgsqlOptions =>
                npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "configuration"))
            .Options;
        return new IHostPro.Contexts.Configuration.Infrastructure.Persistence.ConfigurationDbContext(options, new TenantContext());
    }

    private IHostPro.Contexts.Housekeeping.Infrastructure.Persistence.HousekeepingDbContext CreateHousekeepingDbContext()
    {
        var options = new DbContextOptionsBuilder<IHostPro.Contexts.Housekeeping.Infrastructure.Persistence.HousekeepingDbContext>()
            .UseNpgsql(_migratorConnectionString, npgsqlOptions =>
                npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "housekeeping"))
            .Options;
        return new IHostPro.Contexts.Housekeeping.Infrastructure.Persistence.HousekeepingDbContext(options, new TenantContext());
    }

    /// <summary>Mirrors <see cref="CreateIdentityDbContext"/>'s exact pattern: with no tenantId, the migrator connection (schema DDL only, e.g. <see cref="MigrateSchemasAsync"/>); with one, the app connection and a tenant-scoped <see cref="TenantContext"/>.</summary>
    private IHostPro.Contexts.Reservations.Infrastructure.Persistence.ReservationsDbContext CreateReservationsDbContext(Guid? tenantId = null)
    {
        var tenantContext = new TenantContext();
        if (tenantId is { } id)
            tenantContext.SetTenant(id);
        var options = new DbContextOptionsBuilder<IHostPro.Contexts.Reservations.Infrastructure.Persistence.ReservationsDbContext>()
            .UseNpgsql(tenantId is null ? _migratorConnectionString : _appConnectionString, npgsqlOptions =>
                npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "reservations"))
            .Options;
        return new IHostPro.Contexts.Reservations.Infrastructure.Persistence.ReservationsDbContext(options, tenantContext);
    }

    /// <summary>
    /// Counts <c>reservations.reservation_audit_log</c> rows for one reservation/action —
    /// exposed for tests that must prove a rejected (losing) request produced no audit trail.
    /// <c>ReservationAuditWriter.Record</c> and the domain event enqueue both happen in the same
    /// application-layer code path, and both are persisted (or rolled back) atomically together by
    /// <c>ReservationsOutboxTransactionExecutor</c> (event staged into the same
    /// <c>SaveChangesAndFlushMessagesAsync</c> call as the audit row) — so an audit-row count of
    /// exactly one for a two-request race is direct evidence the loser produced neither.
    ///
    /// Uses the app connection with an explicit <c>SELECT set_config('app.tenant_id', ..., true)</c>
    /// inside its own transaction — mirrors <see cref="SeedTenantAndAdminAsync"/>'s own pattern
    /// exactly. This table is RLS-protected (Fase 3, Incremento 1 plan, item 11); without both the
    /// tenant-scoped <see cref="TenantContext"/> (satisfies the EF Core Global Query Filter) and the
    /// session-level <c>app.tenant_id</c> (satisfies the database-level RLS policy, which the
    /// migrator connection alone does not bypass), the query silently returns zero rows regardless
    /// of what was actually persisted — confirmed the hard way earlier in this same investigation.
    /// </summary>
    public async Task<int> CountReservationAuditEntriesAsync(Guid reservationId, string actionCode)
    {
        await using var dbContext = CreateReservationsDbContext(_tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await dbContext.Database.ExecuteSqlInterpolatedAsync($"SELECT set_config('app.tenant_id', {_tenantId.ToString()}, true)");

        var count = await dbContext.ReservationAuditLog
            .Where(e => e.AggregateId == reservationId && e.ActionCode == actionCode)
            .CountAsync();

        await transaction.CommitAsync();
        return count;
    }

    private static string FindSolutionRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "IHostPro.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException($"Could not locate IHostPro.sln walking up from {AppContext.BaseDirectory}.");
    }

    /// <summary>
    /// Runs the actual built <c>IHostPro.Api</c> executable as a real
    /// subprocess (never re-implemented in-test — same rationale as
    /// <c>WolverineThreeStoreCompositionTests.RunMigrationRunnerAsync</c>),
    /// bound to the fixed port the committed frontend <c>config.json</c>
    /// already expects.
    /// </summary>
    private ManagedProcess StartApiProcess()
    {
        var dllPath = Path.Combine(FindSolutionRoot(), "src", "Host", "IHostPro.Api", "bin", "Debug", "net10.0", "IHostPro.Api.dll");
        if (!File.Exists(dllPath))
            throw new InvalidOperationException($"IHostPro.Api build output not found at {dllPath}. Build IHostPro.Api in Debug configuration first.");

        using var signingKey = RSA.Create(2048);
        var signingKeyPem = signingKey.ExportRSAPrivateKeyPem();

        var psi = new ProcessStartInfo("dotnet", $"\"{dllPath}\" --urls {ApiBaseUrl}");
        psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        psi.Environment["ConnectionStrings__Identity"] = _appConnectionString;
        psi.Environment["ConnectionStrings__PropertyManagement"] = _appConnectionString;
        psi.Environment["ConnectionStrings__Reservations"] = _appConnectionString;
        psi.Environment["ConnectionStrings__Configuration"] = _appConnectionString;
        psi.Environment["ConnectionStrings__Housekeeping"] = _appConnectionString;
        psi.Environment["ConnectionStrings__Platform"] = _appConnectionString;
        psi.Environment["Identity__Jwt__Issuer"] = "https://identity.ihostpro.test";
        psi.Environment["Identity__Jwt__Audience"] = "ihostpro-api-test";
        psi.Environment["Identity__Jwt__AccessTokenLifetime"] = "00:15:00";
        psi.Environment["Identity__Jwt__ClockSkew"] = "00:01:00";
        psi.Environment["Identity__Jwt__SigningKey__PrivateKeyPem"] = signingKeyPem;
        psi.Environment["Identity__AccountLockout__MaxFailedAccessAttempts"] = "5";
        psi.Environment["Identity__AccountLockout__DefaultLockoutDuration"] = "00:05:00";
        psi.Environment["Identity__AccountLockout__AllowedForNewUsers"] = "true";
        psi.Environment["Identity__RefreshToken__Lifetime"] = "30.00:00:00";
        psi.Environment["Identity__RefreshToken__SecretSizeBytes"] = "32";
        psi.Environment["Identity__RefreshToken__ConcurrentRotationGraceWindow"] = "00:00:10";
        psi.Environment["Identity__SessionRevocationCache__ConnectionString"] = _redisContainer.GetConnectionString();
        psi.Environment["Configuration__PolicyCache__ConnectionString"] = _redisContainer.GetConnectionString();
        psi.Environment["RabbitMq__Host"] = _rabbitMqContainer.Hostname;
        psi.Environment["RabbitMq__VirtualHost"] = "/";
        psi.Environment["RabbitMq__Username"] = RabbitMqBuilder.DefaultUsername;
        psi.Environment["RabbitMq__Password"] = RabbitMqBuilder.DefaultPassword;
        // Cors:AllowedOrigins is left at its committed appsettings.json default
        // (http://localhost:4200) — WebBaseUrl below matches it exactly, so
        // no override is needed here.
        psi.Environment["OpenTelemetry__OtlpEndpoint"] = "http://127.0.0.1:14317";

        return ManagedProcess.Start(psi, "IHostPro.Api");
    }

    /// <summary>
    /// Runs the actual built <c>IHostPro.Worker</c> executable as a real
    /// subprocess (same rationale as <see cref="StartApiProcess"/>) — the
    /// real consumer of <c>PolicyUpdated</c>, sharing the exact same
    /// RabbitMQ/Redis connection <see cref="StartApiProcess"/> hands to
    /// <c>IHostPro.Api</c>, so a write through the real API is actually
    /// reflected by a subsequent real-time cache invalidation, exactly like
    /// production. Binds no HTTP port of its own.
    /// </summary>
    private ManagedProcess StartWorkerProcess()
    {
        var dllPath = Path.Combine(FindSolutionRoot(), "src", "Host", "IHostPro.Worker", "bin", "Debug", "net10.0", "IHostPro.Worker.dll");
        if (!File.Exists(dllPath))
            throw new InvalidOperationException($"IHostPro.Worker build output not found at {dllPath}. Build IHostPro.Worker in Debug configuration first.");

        using var signingKey = RSA.Create(2048);
        var signingKeyPem = signingKey.ExportRSAPrivateKeyPem();

        var psi = new ProcessStartInfo("dotnet", $"\"{dllPath}\"");
        psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        psi.Environment["ConnectionStrings__Identity"] = _appConnectionString;
        // Fase 6, Checkpoint 6 homologação: was missing entirely — the real
        // Worker subprocess this fixture starts calls AddHousekeepingModule,
        // which needs ConnectionStrings:Housekeeping to point at THIS
        // fixture's own ephemeral Postgres container (never the
        // appsettings.json dev-default), same as every other context below.
        psi.Environment["ConnectionStrings__Housekeeping"] = _appConnectionString;
        // Fase 6, Checkpoint 6 homologação, real defect found and fixed:
        // ConnectionStrings:Platform was missing entirely from this specific
        // process launch (StartApiProcess already sets it) — Program.cs's
        // own Main Wolverine store setup throws
        // InvalidOperationException("Missing connection string
        // 'ConnectionStrings:Platform'.") immediately at startup without it,
        // confirmed by a real crash log captured from this exact subprocess
        // (temporary Debug-level diagnostic capture, since reverted) —
        // meaning the real Worker this fixture starts has never actually
        // been running successfully; every E2E test that depends on ANY
        // Worker-consumed event (PolicyUpdated included, not just
        // Housekeeping) was silently exercising a Worker that immediately
        // crashed at boot.
        psi.Environment["ConnectionStrings__Platform"] = _appConnectionString;
        // Fase 7, Incremento 1, Checkpoint 3: same class of defect as the
        // Platform one above, found the same way (a real ScheduleAgendaE2ETests
        // run failing every Cleaning-dependent test with a
        // WaitUntilKnownToHousekeepingAsync timeout, root-caused by reading
        // this exact subprocess's own crash — "Missing connection string
        // 'ConnectionStrings:Reservations'."). Real Program.cs enrolls
        // Reservations' own ancillary outbox in the Worker since Fase 7
        // Checkpoint 1 (Agenda Foundation, CleaningScheduleProjection
        // consumer) — the checked-in appsettings.json gained the same
        // missing key in the same investigation (see Fase 7 homologação
        // document, Checkpoint 3), but this fixture's own env var overrides
        // are a separate list and needed the identical fix.
        psi.Environment["ConnectionStrings__Reservations"] = _appConnectionString;
        psi.Environment["Identity__Jwt__Issuer"] = "https://identity.ihostpro.test";
        psi.Environment["Identity__Jwt__Audience"] = "ihostpro-api-test";
        psi.Environment["Identity__Jwt__AccessTokenLifetime"] = "00:15:00";
        psi.Environment["Identity__Jwt__ClockSkew"] = "00:01:00";
        psi.Environment["Identity__Jwt__SigningKey__PrivateKeyPem"] = signingKeyPem;
        psi.Environment["Identity__AccountLockout__MaxFailedAccessAttempts"] = "5";
        psi.Environment["Identity__AccountLockout__DefaultLockoutDuration"] = "00:05:00";
        psi.Environment["Identity__AccountLockout__AllowedForNewUsers"] = "true";
        psi.Environment["Identity__RefreshToken__Lifetime"] = "30.00:00:00";
        psi.Environment["Identity__RefreshToken__SecretSizeBytes"] = "32";
        psi.Environment["Identity__RefreshToken__ConcurrentRotationGraceWindow"] = "00:00:10";
        psi.Environment["Identity__SessionRevocationCache__ConnectionString"] = _redisContainer.GetConnectionString();
        psi.Environment["Configuration__PolicyCache__ConnectionString"] = _redisContainer.GetConnectionString();
        psi.Environment["RabbitMq__Host"] = _rabbitMqContainer.Hostname;
        psi.Environment["RabbitMq__VirtualHost"] = "/";
        psi.Environment["RabbitMq__Username"] = RabbitMqBuilder.DefaultUsername;
        psi.Environment["RabbitMq__Password"] = RabbitMqBuilder.DefaultPassword;
        psi.Environment["OpenTelemetry__OtlpEndpoint"] = "http://127.0.0.1:14317";

        return ManagedProcess.Start(psi, "IHostPro.Worker");
    }

    /// <summary>Real <c>npm start</c> (Angular's own dev server, `ng serve`) as a subprocess — never a hand-built static host standing in for it.</summary>
    private ManagedProcess StartWebProcess()
    {
        var webRoot = Path.Combine(FindSolutionRoot(), "frontend", "IHostPro.Web");
        if (!Directory.Exists(webRoot))
            throw new InvalidOperationException($"Frontend project not found at {webRoot}.");

        // Invokes the Angular CLI's own script directly via `node`, never `npm start`/`ng.cmd`:
        // on Windows, Process.Start(FileName = "npm.cmd", UseShellExecute = false) breaks
        // npm.cmd's own %~dp0-based module resolution (a known Process.Start + .cmd interop
        // pitfall). Wrapping it in `cmd.exe /c npm.cmd start` worked around that, but introduced
        // a worse problem: npm's own multi-hop process spawning (cmd.exe → npm.cmd → npm →
        // ng serve) breaks Process.Kill(entireProcessTree: true) in DisposeAsync below — the
        // real `ng serve` node.exe (and its esbuild.exe child) routinely survived as an orphan
        // holding this process's stdout/stderr pipe open forever, making every run *look* hung
        // long after the actual test run had already finished (observed repeatedly this
        // session). Calling `node ng.js serve` directly makes the tracked Process the actual,
        // single, killable process — no intermediate shell hops to lose track of.
        var ngScript = Path.Combine(webRoot, "node_modules", "@angular", "cli", "bin", "ng.js");
        var psi = new ProcessStartInfo("node", $"\"{ngScript}\" serve --port {WebPort}")
        {
            WorkingDirectory = webRoot,
        };

        return ManagedProcess.Start(psi, "ng serve");
    }

    /// <summary>
    /// Opens the given (closed) connection, retrying a bounded number of times with a short
    /// delay if the very first attempt times out — bridges the readiness-vs-port-publish race
    /// described where <see cref="StartPostgresAsync"/> calls this. Any other exception, or the
    /// final attempt's exception, propagates immediately: a genuinely unreachable database must
    /// still fail the fixture, never be silently retried away.
    /// </summary>
    private static async Task OpenWithRetryAsync(NpgsqlConnection connection, int maxAttempts = 5)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await connection.OpenAsync();
                return;
            }
            catch (NpgsqlException) when (attempt < maxAttempts)
            {
                await Task.Delay(TimeSpan.FromSeconds(attempt));
            }
        }
    }

    private static async Task WaitForHttpReadyAsync(string url, TimeSpan timeout)
    {
        using var httpClient = new HttpClient();
        var deadline = DateTimeOffset.UtcNow + timeout;
        Exception? lastError = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                using var response = await httpClient.GetAsync(url);
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or SocketException or TaskCanceledException)
            {
                lastError = ex;
                await Task.Delay(TimeSpan.FromMilliseconds(500));
            }
        }
        throw new TimeoutException($"'{url}' did not become reachable within {timeout}.", lastError);
    }
}
