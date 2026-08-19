using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;
using Testcontainers.Redis;

namespace IHostPro.Api.Tests.Integration;

/// <summary>
/// Fase 5 (Configuration and Policy), Checkpoint 7 homologação — preventive
/// regression test for every real Wolverine discovery/codegen defect found
/// and fixed against IHostPro.Worker's real, unmodified startup during that
/// checkpoint (see the homologation document, §13.7-§13.11):
///
///   (a) opts.Publish(...) silently never listening to anything — fixed by
///       opts.ListenToRabbitQueue("configuration.policy-updated") (§13.7);
///   (b) TenantResolutionMiddleware unresolvable by Wolverine's generated
///       code, first as an open generic, then as a base-type reference —
///       fixed by a concrete, non-generic static Before(...) method
///       (§13.8/§13.9);
///   (c) RedisPolicyValueCache's `internal` accessibility making it
///       unconstructable from Wolverine's cross-assembly generated code —
///       fixed by making the class public, with explicit interface
///       implementations (§13.10);
///   (d) PolicyUpdatedCacheInvalidationHandler being discovered a SECOND
///       time as its own Wolverine handler purely because its name ended in
///       "Handler" — fixed by renaming it to PolicyUpdatedCacheInvalidation,
///       a plain business service, never a transport adapter (§13.11).
///
/// IHostPro.Worker is a Worker Service (Microsoft.NET.Sdk.Worker), not an
/// ASP.NET Core web host — WebApplicationFactory&lt;TEntryPoint&gt;, this
/// project's own established technique for exercising IHostPro.Api's real
/// composition root in-process (see WolverineThreeStoreCompositionTests),
/// does not apply to it. Wolverine's own CLI diagnostics
/// (`wolverine-diagnostics describe-handlers`/`describe-routing`) would
/// require Program.cs to call host.RunOaktonCommands(args) instead of
/// host.Run() — a production code change this checkpoint's own closing
/// instruction forbids ("não alterar novamente a lógica funcional do
/// Worker/cache"). Instead, this spawns the real, unmodified compiled
/// IHostPro.Worker.dll as a subprocess — the same established pattern
/// WolverineThreeStoreCompositionTests.RunMigrationRunnerAsync and
/// WebE2EFixture.StartWorkerProcess already use to exercise a real,
/// unmodified executable — and asserts on its own Serilog console output.
/// Wolverine performs handler discovery and lazy code generation eagerly at
/// UseWolverine(...)/host.Build() time, before host.Run() ever returns
/// control, so a real subprocess boot exercises the full
/// discovery-through-compiled-codegen pipeline end to end; a reflection-only
/// match report would not have caught the historical CS0128 duplicate-
/// generated-method compile failure, which only manifested when Wolverine
/// actually generated and compiled the chain. No private Wolverine field is
/// ever accessed, and no production API was added solely for this test.
/// </summary>
public sealed class PolicyUpdatedWolverineDiscoveryTests : IAsyncLifetime
{
    private const string AppRolePassword = "test_app_password";
    private const string MigratorRolePassword = "test_migrator_password";

    private PostgreSqlContainer _postgresContainer = null!;
    private RabbitMqContainer _rabbitMqContainer = null!;
    private RedisContainer _redisContainer = null!;
    private string _migratorConnectionString = null!;
    private string _appConnectionString = null!;

    public async Task InitializeAsync()
    {
        _postgresContainer = new PostgreSqlBuilder()
            .WithImage("postgres:16")
            .WithDatabase("ihostpro_test")
            .WithUsername("ihostpro")
            .WithPassword("ihostpro_dev")
            .Build();

        // UseIHostProRabbitMq (Program.cs's own real RabbitMQ wiring,
        // exercised unmodified below) has no port override — always the
        // default AMQP port 5672 — same rationale as
        // WolverineThreeStoreCompositionTests.Fixture's identical binding.
        _rabbitMqContainer = new RabbitMqBuilder()
            .WithImage("rabbitmq:3-management-alpine")
            .WithPortBinding(5672, 5672)
            .Build();

        _redisContainer = new RedisBuilder()
            .WithImage("redis:7-alpine")
            .Build();

        await Task.WhenAll(
            _postgresContainer.StartAsync(),
            _rabbitMqContainer.StartAsync(),
            _redisContainer.StartAsync());

        var adminConnectionString = _postgresContainer.GetConnectionString();
        await using (var adminConnection = new Npgsql.NpgsqlConnection(adminConnectionString))
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

        var builder = new Npgsql.NpgsqlConnectionStringBuilder(adminConnectionString) { Username = "ihostpro_migrator", Password = MigratorRolePassword };
        _migratorConnectionString = builder.ConnectionString;
        builder.Username = "ihostpro_app";
        builder.Password = AppRolePassword;
        _appConnectionString = builder.ConnectionString;

        // Fase 7, Incremento 2, Checkpoint 1 — real defect found and fixed in
        // this TEST's own fixture (not production code): this method used to
        // hand-declare only the exact RabbitMQ topology it already knew
        // about, and needed patching every time a new Bounded Context's
        // Worker-hosted consumers grew (already patched once for
        // Housekeeping's own two queues — see the Fase 6 homologação
        // narrative this class's own doc comment describes). Dashboard added
        // a THIRD ancillary Postgresql store plus four more Worker-hosted
        // queues, and this fixture's own hand-rolled Postgres container had
        // no schema/role at all (no MigrationRunner ever ran against it) —
        // replaced with the same MigrationRunner-based provisioning every
        // other real-Worker-subprocess test in this project already uses,
        // which declares the complete topology (including Dashboard's own
        // four queues) and provisions every schema in one step, so this
        // fixture never needs hand-patching again as new Bounded Contexts
        // are added.
        var (exitCode, output) = await RunMigrationRunnerAsync();
        if (exitCode != 0)
            throw new InvalidOperationException($"MigrationRunner failed with exit code {exitCode}. Output:\n{output}");
    }

    public async Task DisposeAsync()
    {
        await _rabbitMqContainer.DisposeAsync();
        await _redisContainer.DisposeAsync();
        await _postgresContainer.DisposeAsync();
    }

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
        psi.Environment["ConnectionStrings__Identity"] = _migratorConnectionString;
        psi.Environment["ConnectionStrings__PropertyManagement"] = _migratorConnectionString;
        psi.Environment["ConnectionStrings__Reservations"] = _migratorConnectionString;
        psi.Environment["ConnectionStrings__Configuration"] = _migratorConnectionString;
        psi.Environment["ConnectionStrings__Housekeeping"] = _migratorConnectionString;
        psi.Environment["ConnectionStrings__Communication"] = _migratorConnectionString;
        psi.Environment["ConnectionStrings__Dashboard"] = _migratorConnectionString;
        psi.Environment["ConnectionStrings__Platform"] = _migratorConnectionString;
        psi.Environment["RabbitMq__Host"] = _rabbitMqContainer.Hostname;
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

    [Fact]
    public async Task IHostPro_Worker_boots_and_listens_to_configuration_policy_updated_with_no_wolverine_discovery_or_codegen_failure()
    {
        var dllPath = Path.Combine(FindSolutionRoot(), "src", "Host", "IHostPro.Worker", "bin", "Debug", "net10.0", "IHostPro.Worker.dll");
        if (!File.Exists(dllPath))
            throw new InvalidOperationException($"IHostPro.Worker build output not found at {dllPath}. Build IHostPro.Worker in Debug configuration first.");

        // Identity/AccountLockout/RefreshToken values below have no bearing on
        // this test's subject (Wolverine discovery/codegen for PolicyUpdated)
        // — they exist only because AddIdentityModule's JwtOptions/
        // AccountLockoutOptions/RefreshTokenOptions are eagerly
        // ValidateOnStart-ed regardless of environment, so the process would
        // otherwise fail for a reason unrelated to what this test verifies.
        // Same exact recipe as WebE2EFixture.StartWorkerProcess, already
        // proven to boot the real IHostPro.Worker.dll successfully.
        using var signingKey = RSA.Create(2048);
        var signingKeyPem = signingKey.ExportRSAPrivateKeyPem();

        var psi = new ProcessStartInfo("dotnet", $"\"{dllPath}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        psi.Environment["DOTNET_ENVIRONMENT"] = "Development";
        psi.Environment["ConnectionStrings__Identity"] = _appConnectionString;
        psi.Environment["ConnectionStrings__Housekeeping"] = _appConnectionString;
        psi.Environment["ConnectionStrings__Reservations"] = _appConnectionString;
        psi.Environment["ConnectionStrings__Communication"] = _appConnectionString;
        psi.Environment["ConnectionStrings__Dashboard"] = _appConnectionString;
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
        psi.Environment["Configuration__PolicyCache__ConnectionString"] = _redisContainer.GetConnectionString();
        psi.Environment["RabbitMq__Host"] = _rabbitMqContainer.Hostname;
        psi.Environment["RabbitMq__VirtualHost"] = "/";
        psi.Environment["RabbitMq__Username"] = RabbitMqBuilder.DefaultUsername;
        psi.Environment["RabbitMq__Password"] = RabbitMqBuilder.DefaultPassword;
        psi.Environment["OpenTelemetry__OtlpEndpoint"] = "http://127.0.0.1:14318";

        using var process = new Process { StartInfo = psi };
        var output = new StringBuilder();
        var outputLock = new object();
        var startedSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failureSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string? failureLine = null;

        // Empirically-confirmed signatures from this checkpoint's own live
        // debugging (§13.8-§13.11) — any one of these means Wolverine failed
        // to discover, resolve or compile the PolicyUpdated handler chain.
        string[] knownFailureSignatures =
        [
            "UnResolvableVariableException",
            "InvalidServiceLocationException",
            "error CS0128",
            "Exception detected",
        ];

        void OnLine(string? line)
        {
            if (line is null)
                return;

            lock (outputLock)
                output.AppendLine(line);

            if (line.Contains("Started message listening at rabbitmq://queue/configuration.policy-updated", StringComparison.Ordinal))
                startedSignal.TrySetResult();

            foreach (var signature in knownFailureSignatures)
            {
                if (line.Contains(signature, StringComparison.Ordinal))
                {
                    failureLine = line;
                    failureSignal.TrySetResult();
                    break;
                }
            }
        }

        process.OutputDataReceived += (_, e) => OnLine(e.Data);
        process.ErrorDataReceived += (_, e) => OnLine(e.Data);

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            var completed = await Task.WhenAny(
                startedSignal.Task,
                failureSignal.Task,
                Task.Delay(TimeSpan.FromSeconds(60)));

            string FullOutput()
            {
                lock (outputLock)
                    return output.ToString();
            }

            if (completed == failureSignal.Task)
                Assert.Fail($"IHostPro.Worker reported a known Wolverine discovery/codegen failure signature ('{failureLine}'). Full output:\n{FullOutput()}");

            if (completed != startedSignal.Task)
                Assert.Fail($"IHostPro.Worker did not report listening to configuration.policy-updated within 60s. Full output:\n{FullOutput()}");

            // Redundant, explicit check against the captured output rather
            // than trusting only the absence of a failure signal — §13.11's
            // exact defect (PolicyUpdatedCacheInvalidation matched a second
            // time by Wolverine's own naming convention) is the one failure
            // mode that would NOT necessarily throw at startup; it would
            // instead register a second, silently redundant handler for the
            // same message type. A second "Started message listening..."
            // line for the same queue is this test's direct evidence that
            // never happened.
            var startedLineCount = FullOutput()
                .Split('\n')
                .Count(l => l.Contains("Started message listening at rabbitmq://queue/configuration.policy-updated", StringComparison.Ordinal));
            startedLineCount.Should().Be(1, "PolicyUpdated must have exactly one Wolverine listener, never a second one from an accidentally-discovered handler");
        }
        finally
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Process already exited between the HasExited check and Kill.
            }
            await process.WaitForExitAsync();
        }
    }

    private static string FindSolutionRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "IHostPro.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException($"Could not locate IHostPro.sln walking up from {AppContext.BaseDirectory}.");
    }
}
