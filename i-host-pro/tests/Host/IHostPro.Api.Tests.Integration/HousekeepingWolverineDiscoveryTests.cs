using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;

namespace IHostPro.Api.Tests.Integration;

/// <summary>
/// Fase 6 (Housekeeping), Checkpoint 6 homologação — preventive regression
/// test mirroring <see cref="PolicyUpdatedWolverineDiscoveryTests"/>'s own
/// established methodology exactly, generalized to all six of Housekeeping's
/// real, consumed Integration Events after the ADR-015 execution-boundary
/// migration (<c>ReservationCreated</c>/<c>ReservationCancelled</c>/
/// <c>PropertyCreated</c>/<c>PropertyActivated</c>/<c>PropertyDeactivated</c>/
/// <c>PropertyArchived</c>). Spawns the real, unmodified compiled
/// <c>IHostPro.Worker.dll</c> as a subprocess and asserts on its own Serilog
/// console output — no private Wolverine field is ever accessed, and no
/// production API was added solely for this test. Confirms:
///
///   (a) no known Wolverine discovery/codegen failure signature is ever
///       logged (the same signatures Fase 5's own investigation catalogued —
///       <c>UnResolvableVariableException</c>/<c>InvalidServiceLocationException</c>/
///       <c>error CS0128</c>/a generic "Exception detected" — plus
///       <c>Cannot build service type</c>, the exact signature of Defeito A,
///       this phase's own real, previously-observed defect);
///   (b) each of the two Housekeeping queues
///       (<c>housekeeping.reservation-projection</c>/
///       <c>housekeeping.property-projection</c>) reports EXACTLY one
///       Wolverine listener — never a second one from a handler accidentally
///       discovered a second time by Wolverine's own naming convention (the
///       exact Fase 5 §13.11 defect class this test generalizes the guard
///       for).
/// </summary>
public sealed class HousekeepingWolverineDiscoveryTests : IAsyncLifetime
{
    private const string AppRolePassword = "test_app_password";
    private const string MigratorRolePassword = "test_migrator_password";

    private PostgreSqlContainer _postgresContainer = null!;
    private RabbitMqContainer _rabbitMqContainer = null!;
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
        await _postgresContainer.StartAsync();

        _rabbitMqContainer = new RabbitMqBuilder()
            .WithImage("rabbitmq:3-management-alpine")
            .WithPortBinding(5672, 5672)
            .Build();
        await _rabbitMqContainer.StartAsync();

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

        var (exitCode, output) = await RunMigrationRunnerAsync();
        if (exitCode != 0)
            throw new InvalidOperationException($"MigrationRunner failed with exit code {exitCode}. Output:\n{output}");
    }

    public async Task DisposeAsync()
    {
        await _rabbitMqContainer.DisposeAsync();
        await _postgresContainer.DisposeAsync();
    }

    [Fact]
    public async Task IHostPro_Worker_boots_and_listens_to_both_Housekeeping_queues_with_no_wolverine_discovery_or_codegen_failure_and_no_duplicate_listener()
    {
        var dllPath = Path.Combine(FindSolutionRoot(), "src", "Host", "IHostPro.Worker", "bin", "Debug", "net10.0", "IHostPro.Worker.dll");
        if (!File.Exists(dllPath))
            throw new InvalidOperationException($"IHostPro.Worker build output not found at {dllPath}. Build IHostPro.Worker in Debug configuration first.");

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
        psi.Environment["ConnectionStrings__PropertyManagement"] = _appConnectionString;
        psi.Environment["ConnectionStrings__Reservations"] = _appConnectionString;
        psi.Environment["ConnectionStrings__Configuration"] = _appConnectionString;
        psi.Environment["ConnectionStrings__Housekeeping"] = _appConnectionString;
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
        psi.Environment["Configuration__PolicyCache__ConnectionString"] = "localhost:6379";
        psi.Environment["RabbitMq__Host"] = _rabbitMqContainer.Hostname;
        psi.Environment["RabbitMq__VirtualHost"] = "/";
        psi.Environment["RabbitMq__Username"] = RabbitMqBuilder.DefaultUsername;
        psi.Environment["RabbitMq__Password"] = RabbitMqBuilder.DefaultPassword;
        psi.Environment["OpenTelemetry__OtlpEndpoint"] = "http://127.0.0.1:14323";

        using var process = new Process { StartInfo = psi };
        var output = new StringBuilder();
        var outputLock = new object();
        var reservationStartedSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var propertyStartedSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failureSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string? failureLine = null;

        // Empirically-confirmed signatures from this phase's own real
        // investigation (Checkpoint 6 tenant-identity/outbox-enrollment
        // defects) plus Fase 5's own catalogue — any one of these means
        // Wolverine failed to discover, resolve or compile a handler chain.
        string[] knownFailureSignatures =
        [
            "UnResolvableVariableException",
            "InvalidServiceLocationException",
            "error CS0128",
            "Cannot build service type",
            "Exception detected",
        ];

        void OnLine(string? line)
        {
            if (line is null)
                return;

            lock (outputLock)
                output.AppendLine(line);

            if (line.Contains("Started message listening at rabbitmq://queue/housekeeping.reservation-projection", StringComparison.Ordinal))
                reservationStartedSignal.TrySetResult();
            if (line.Contains("Started message listening at rabbitmq://queue/housekeeping.property-projection", StringComparison.Ordinal))
                propertyStartedSignal.TrySetResult();

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
                Task.WhenAll(reservationStartedSignal.Task, propertyStartedSignal.Task),
                failureSignal.Task,
                Task.Delay(TimeSpan.FromSeconds(60)));

            string FullOutput()
            {
                lock (outputLock)
                    return output.ToString();
            }

            if (completed == failureSignal.Task)
                Assert.Fail($"IHostPro.Worker reported a known Wolverine discovery/codegen failure signature ('{failureLine}'). Full output:\n{FullOutput()}");

            reservationStartedSignal.Task.IsCompleted.Should().BeTrue($"IHostPro.Worker did not report listening to housekeeping.reservation-projection within 60s. Full output:\n{FullOutput()}");
            propertyStartedSignal.Task.IsCompleted.Should().BeTrue($"IHostPro.Worker did not report listening to housekeeping.property-projection within 60s. Full output:\n{FullOutput()}");

            var fullOutput = FullOutput();
            var reservationListenerCount = fullOutput.Split('\n')
                .Count(l => l.Contains("Started message listening at rabbitmq://queue/housekeeping.reservation-projection", StringComparison.Ordinal));
            var propertyListenerCount = fullOutput.Split('\n')
                .Count(l => l.Contains("Started message listening at rabbitmq://queue/housekeeping.property-projection", StringComparison.Ordinal));

            reservationListenerCount.Should().Be(1, "housekeeping.reservation-projection must have exactly one Wolverine listener, never a second one from an accidentally-discovered handler");
            propertyListenerCount.Should().Be(1, "housekeeping.property-projection must have exactly one Wolverine listener, never a second one from an accidentally-discovered handler");
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

    private static string FindSolutionRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "IHostPro.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException($"Could not locate IHostPro.sln walking up from {AppContext.BaseDirectory}.");
    }
}
