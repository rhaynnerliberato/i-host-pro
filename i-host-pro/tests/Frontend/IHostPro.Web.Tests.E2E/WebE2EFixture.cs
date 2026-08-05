using System.Diagnostics;
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

    public string ApiBaseUrl { get; } = $"http://localhost:{ApiPort}";
    public string WebBaseUrl { get; } = $"http://localhost:{WebPort}";

    private PostgreSqlContainer _postgresContainer = null!;
    private RabbitMqContainer _rabbitMqContainer = null!;
    private RedisContainer _redisContainer = null!;
    private string _migratorConnectionString = null!;
    private string _appConnectionString = null!;
    private Process? _apiProcess;
    private Process? _webProcess;
    private IPlaywright _playwright = null!;
    public IBrowser Browser { get; private set; } = null!;

    public async Task InitializeAsync()
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

        _webProcess = StartWebProcess();
        await WaitForHttpReadyAsync(WebBaseUrl, TimeSpan.FromSeconds(90));

        Microsoft.Playwright.Program.Main(["install", "chromium"]);
        _playwright = await Playwright.CreateAsync();
        Browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
    }

    public async Task DisposeAsync()
    {
        if (Browser is not null)
            await Browser.CloseAsync();
        _playwright?.Dispose();

        await StopProcessAsync(_webProcess);
        await StopProcessAsync(_apiProcess);

        if (_rabbitMqContainer is not null)
            await _rabbitMqContainer.DisposeAsync();
        if (_redisContainer is not null)
            await _redisContainer.DisposeAsync();
        if (_postgresContainer is not null)
            await _postgresContainer.DisposeAsync();
    }

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
            await adminConnection.OpenAsync();
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
    }

    /// <summary>Mirrors IHostPro.MigrationRunner exactly: platform_messaging (Main) first, then the three Ancillary outboxes.</summary>
    private async Task ProvisionMessageStoresAsync()
    {
        await ProvisionMessageStoreSchemaAsync("platform_messaging", dbContextType: null);
        await ProvisionMessageStoreSchemaAsync("identity_messaging", typeof(IdentityDbContext));
        await ProvisionMessageStoreSchemaAsync("property_management_messaging", typeof(IHostPro.Contexts.PropertyManagement.Infrastructure.Persistence.PropertyManagementDbContext));
        await ProvisionMessageStoreSchemaAsync("reservations_messaging", typeof(IHostPro.Contexts.Reservations.Infrastructure.Persistence.ReservationsDbContext));
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
                .DeclareExchange("property-management-events", exchange => exchange.ExchangeType = ExchangeType.Topic)
                .DeclareExchange("reservation-events", exchange => exchange.ExchangeType = ExchangeType.Topic);
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
        await using var dbContext = CreateIdentityDbContext(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await dbContext.Database.ExecuteSqlInterpolatedAsync($"SELECT set_config('app.tenant_id', {tenantId.ToString()}, true)");

        var tenant = Tenant.Provision(tenantId, TenantSlug.Create(TenantSlugValue), "E2E Playwright Tenant", DateTimeOffset.UtcNow);
        dbContext.Tenants.Add(tenant);

        var hasher = new Argon2PasswordHasher(new KonsciousArgon2idPrimitive(), Options.Create(new Argon2Options()));
        var hash = PasswordHash.FromEncoded(hasher.HashPassword(null!, AdminPassword));
        var now = DateTimeOffset.UtcNow;
        var user = User.Register(Guid.NewGuid(), tenantId, Email.Create(AdminEmail), AdminFullName, hash, now);
        dbContext.Users.Add(user);
        dbContext.UserRoles.Add(new UserRole(tenantId, user.Id, "ADMIN", now, assignedByUserId: null));

        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();
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

    private IHostPro.Contexts.Reservations.Infrastructure.Persistence.ReservationsDbContext CreateReservationsDbContext()
    {
        var options = new DbContextOptionsBuilder<IHostPro.Contexts.Reservations.Infrastructure.Persistence.ReservationsDbContext>()
            .UseNpgsql(_migratorConnectionString, npgsqlOptions =>
                npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "reservations"))
            .Options;
        return new IHostPro.Contexts.Reservations.Infrastructure.Persistence.ReservationsDbContext(options, new TenantContext());
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
    private Process StartApiProcess()
    {
        var dllPath = Path.Combine(FindSolutionRoot(), "src", "Host", "IHostPro.Api", "bin", "Debug", "net10.0", "IHostPro.Api.dll");
        if (!File.Exists(dllPath))
            throw new InvalidOperationException($"IHostPro.Api build output not found at {dllPath}. Build IHostPro.Api in Debug configuration first.");

        using var signingKey = RSA.Create(2048);
        var signingKeyPem = signingKey.ExportRSAPrivateKeyPem();

        var psi = new ProcessStartInfo("dotnet", $"\"{dllPath}\" --urls {ApiBaseUrl}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        psi.Environment["ConnectionStrings__Identity"] = _appConnectionString;
        psi.Environment["ConnectionStrings__PropertyManagement"] = _appConnectionString;
        psi.Environment["ConnectionStrings__Reservations"] = _appConnectionString;
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
        psi.Environment["RabbitMq__Host"] = _rabbitMqContainer.Hostname;
        psi.Environment["RabbitMq__VirtualHost"] = "/";
        psi.Environment["RabbitMq__Username"] = RabbitMqBuilder.DefaultUsername;
        psi.Environment["RabbitMq__Password"] = RabbitMqBuilder.DefaultPassword;
        // Cors:AllowedOrigins is left at its committed appsettings.json default
        // (http://localhost:4200) — WebBaseUrl below matches it exactly, so
        // no override is needed here.
        psi.Environment["OpenTelemetry__OtlpEndpoint"] = "http://127.0.0.1:14317";

        var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start IHostPro.Api process.");
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, _) => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    /// <summary>Real <c>npm start</c> (Angular's own dev server, `ng serve`) as a subprocess — never a hand-built static host standing in for it.</summary>
    private Process StartWebProcess()
    {
        var webRoot = Path.Combine(FindSolutionRoot(), "frontend", "IHostPro.Web");
        if (!Directory.Exists(webRoot))
            throw new InvalidOperationException($"Frontend project not found at {webRoot}.");

        var npmCommand = OperatingSystem.IsWindows() ? "npm.cmd" : "npm";
        var psi = new ProcessStartInfo(npmCommand, $"start -- --port {WebPort}")
        {
            WorkingDirectory = webRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start the Angular dev server process.");
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, _) => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
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

    private static async Task StopProcessAsync(Process? process)
    {
        if (process is null || process.HasExited)
            return;
        try
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }
        catch (InvalidOperationException)
        {
            // Already exited between the HasExited check and Kill().
        }
    }
}
