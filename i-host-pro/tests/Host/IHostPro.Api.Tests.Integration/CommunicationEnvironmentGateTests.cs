using System.Diagnostics;
using System.Security.Cryptography;
using FluentAssertions;
using Npgsql;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;

namespace IHostPro.Api.Tests.Integration;

/// <summary>
/// Fase 9, Checkpoint 1 — corrective homologation (Production safety gate,
/// found deficient in a post-publication review of commit
/// <c>ae78af5</c>): proves the environment-gated fake WhatsApp automation is
/// an ALLOWLIST (<c>IsDevelopment()</c>), never the earlier, incorrect
/// denylist (<c>!IsProduction()</c>) — the first corrective pass would have
/// left Staging/QA/UAT/any custom environment name with the fake connector
/// still active, the exact false-positive risk the gate exists to close.
///
/// Three environments, one shared setup (fresh Postgres + RabbitMQ), each
/// exercising BOTH gates this checkpoint added:
/// <list type="bullet">
/// <item>IHostPro.MigrationRunner's own <c>reservation-events</c> exchange
/// binding for <c>communication.reservation-created-trigger</c> (Program.cs,
/// same <c>IsDevelopment()</c> condition) — checked directly against the
/// real broker via <c>QueueDeclarePassiveAsync</c>, independent of whether
/// the real <c>IHostPro.Worker.dll</c> subprocess ever runs.</item>
/// <item>IHostPro.Worker's own DI registration + Wolverine listener for
/// Communication's <c>ReservationCreated</c> consumer — checked via the
/// real Worker subprocess's own startup log output, the same
/// "Started message listening at rabbitmq://queue/…" line Wolverine itself
/// emits (mirrors <see cref="HousekeepingWolverineDiscoveryTests"/>/
/// <see cref="PolicyUpdatedWolverineDiscoveryTests"/>).</item>
/// </list>
///
/// Development proves both are ACTIVE. Staging and Production each prove
/// both are ABSENT — Staging specifically because <c>!IsProduction()</c>
/// would have wrongly left it active, Production because it is the
/// deployment target this gate protects — while also proving the Worker
/// process itself starts normally and Housekeeping's own, unrelated
/// consumer keeps listening in every environment: this gate must never
/// prevent the rest of the platform from starting.
/// </summary>
public sealed class CommunicationEnvironmentGateTests : IAsyncLifetime
{
    private const string AppRolePassword = "test_app_password";
    private const string MigratorRolePassword = "test_migrator_password";
    private const string CommunicationQueueName = "communication.reservation-created-trigger";

    private PostgreSqlContainer _postgresContainer = null!;
    private RabbitMqContainer _rabbitMqContainer = null!;
    private string _migratorConnectionString = null!;
    private string _appConnectionString = null!;
    private Process? _workerProcess;

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
        _migratorConnectionString = builder.ConnectionString;
        builder.Username = "ihostpro_app";
        builder.Password = AppRolePassword;
        _appConnectionString = builder.ConnectionString;
    }

    public async Task DisposeAsync()
    {
        if (_workerProcess is { HasExited: false })
        {
            try { _workerProcess.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            await _workerProcess.WaitForExitAsync();
        }
        _workerProcess?.Dispose();

        await _rabbitMqContainer.DisposeAsync();
        await _postgresContainer.DisposeAsync();
    }

    [Fact]
    public async Task Development_registers_and_activates_the_fake_connector_and_Communications_listener()
    {
        await RunScenarioAsync(environmentName: "Development", expectCommunicationActive: true);
    }

    [Fact]
    public async Task Staging_registers_neither_the_fake_connector_nor_Communications_listener()
    {
        // The environment name most likely to have been wrongly left active
        // by the earlier, incorrect !IsProduction() gate — this is the
        // scenario that specific defect would have failed.
        await RunScenarioAsync(environmentName: "Staging", expectCommunicationActive: false);
    }

    [Fact]
    public async Task Production_registers_neither_the_fake_connector_nor_Communications_listener_and_the_Worker_still_starts_normally()
    {
        await RunScenarioAsync(environmentName: "Production", expectCommunicationActive: false);
    }

    private async Task RunScenarioAsync(string environmentName, bool expectCommunicationActive)
    {
        var (migrationExitCode, migrationOutput) = await RunMigrationRunnerAsync(environmentName);
        migrationExitCode.Should().Be(0, $"MigrationRunner must succeed under {environmentName}. Output:\n{migrationOutput}");

        var communicationQueueProvisioned = await CommunicationQueueExistsAsync();
        communicationQueueProvisioned.Should().Be(expectCommunicationActive,
            $"IHostPro.MigrationRunner must provision the {CommunicationQueueName} queue/binding only under Development (environment under test: {environmentName})");

        StartWorkerProcess(environmentName);

        var housekeepingReady = await WaitForWorkerLogLineAsync(
            "Started message listening at rabbitmq://queue/housekeeping.reservation-projection", TimeSpan.FromSeconds(30));
        housekeepingReady.Should().BeTrue(
            $"the Worker process must start normally and Housekeeping's own, unrelated consumer must keep listening under {environmentName} — this gate must never affect the rest of the platform");

        var communicationReady = await WaitForWorkerLogLineAsync(
            $"Started message listening at rabbitmq://queue/{CommunicationQueueName}", TimeSpan.FromSeconds(5));
        communicationReady.Should().Be(expectCommunicationActive,
            $"IHostPro.Worker must register/listen to Communication's own queue only under Development (environment under test: {environmentName})");

        _workerProcess!.HasExited.Should().BeFalse($"the Worker process must remain running under {environmentName}, never crash because Communication is gated off");
    }

    private async Task<bool> CommunicationQueueExistsAsync()
    {
        var connectionFactory = new ConnectionFactory
        {
            HostName = _rabbitMqContainer.Hostname,
            UserName = RabbitMqBuilder.DefaultUsername,
            Password = RabbitMqBuilder.DefaultPassword,
            VirtualHost = "/",
        };

        await using var connection = await connectionFactory.CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();

        try
        {
            // QueueDeclarePassive checks existence without creating anything
            // — the broker closes the channel with a 404 NOT_FOUND if the
            // queue does not exist, which RabbitMQ.Client surfaces as
            // OperationInterruptedException.
            await channel.QueueDeclarePassiveAsync(CommunicationQueueName);
            return true;
        }
        catch (OperationInterruptedException)
        {
            return false;
        }
    }

    // ---- Worker subprocess --------------------------------------------

    private readonly System.Text.StringBuilder _workerOutput = new();
    private readonly object _workerOutputLock = new();
    private readonly List<TaskCompletionSource<bool>> _workerLineWaiters = [];
    private readonly List<string> _workerLineWaiterPatterns = [];

    private void StartWorkerProcess(string environmentName)
    {
        var dllPath = Path.Combine(FindSolutionRoot(), "src", "Host", "IHostPro.Worker", "bin", "Debug", "net10.0", "IHostPro.Worker.dll");
        if (!File.Exists(dllPath))
            throw new InvalidOperationException($"IHostPro.Worker build output not found at {dllPath}. Build IHostPro.Worker in Debug configuration first.");

        using var signingKey = RSA.Create(2048);
        var psi = new ProcessStartInfo("dotnet", $"\"{dllPath}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var (key, value) in BuildWorkerEnvironment(environmentName, signingKey.ExportRSAPrivateKeyPem()))
            psi.Environment[key] = value;

        _workerProcess = new Process { StartInfo = psi };
        _workerProcess.OutputDataReceived += (_, e) => OnWorkerLine(e.Data);
        _workerProcess.ErrorDataReceived += (_, e) => OnWorkerLine(e.Data);
        _workerProcess.Start();
        _workerProcess.BeginOutputReadLine();
        _workerProcess.BeginErrorReadLine();
    }

    private void OnWorkerLine(string? line)
    {
        if (line is null) return;
        lock (_workerOutputLock)
        {
            _workerOutput.AppendLine(line);
            for (var i = 0; i < _workerLineWaiterPatterns.Count; i++)
            {
                if (line.Contains(_workerLineWaiterPatterns[i], StringComparison.Ordinal))
                    _workerLineWaiters[i].TrySetResult(true);
            }
        }
    }

    private async Task<bool> WaitForWorkerLogLineAsync(string pattern, TimeSpan timeout)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_workerOutputLock)
        {
            if (_workerOutput.ToString().Contains(pattern, StringComparison.Ordinal))
                return true;
            _workerLineWaiterPatterns.Add(pattern);
            _workerLineWaiters.Add(tcs);
        }

        return await Task.WhenAny(tcs.Task, Task.Delay(timeout)) == tcs.Task;
    }

    // DOTNET_ENVIRONMENT is the variable IHostPro.Worker's Generic Host
    // (Host.CreateApplicationBuilder) actually reads — ASPNETCORE_ENVIRONMENT
    // is kept alongside it too (harmless, at no cost) purely for parity with
    // every other fixture in this assembly, never because Worker reads it.
    private Dictionary<string, string?> BuildWorkerEnvironment(string environmentName, string signingKeyPem) => new()
    {
        ["ASPNETCORE_ENVIRONMENT"] = environmentName,
        ["DOTNET_ENVIRONMENT"] = environmentName,
        ["ConnectionStrings__Identity"] = _appConnectionString,
        ["ConnectionStrings__PropertyManagement"] = _appConnectionString,
        ["ConnectionStrings__Reservations"] = _appConnectionString,
        ["ConnectionStrings__Configuration"] = _appConnectionString,
        ["ConnectionStrings__Housekeeping"] = _appConnectionString,
        ["ConnectionStrings__Dashboard"] = _appConnectionString,
        ["ConnectionStrings__Communication"] = _appConnectionString,
        ["ConnectionStrings__Platform"] = _appConnectionString,
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
        ["Configuration__PolicyCache__ConnectionString"] = "localhost:6379",
        ["RabbitMq__Host"] = _rabbitMqContainer.Hostname,
        ["RabbitMq__VirtualHost"] = "/",
        ["RabbitMq__Username"] = RabbitMqBuilder.DefaultUsername,
        ["RabbitMq__Password"] = RabbitMqBuilder.DefaultPassword,
        ["OpenTelemetry__OtlpEndpoint"] = "http://127.0.0.1:14321",
    };

    private async Task<(int ExitCode, string Output)> RunMigrationRunnerAsync(string environmentName)
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
        psi.Environment["ASPNETCORE_ENVIRONMENT"] = environmentName;
        psi.Environment["DOTNET_ENVIRONMENT"] = environmentName;
        psi.Environment["ConnectionStrings__Identity"] = _migratorConnectionString;
        psi.Environment["ConnectionStrings__PropertyManagement"] = _migratorConnectionString;
        psi.Environment["ConnectionStrings__Reservations"] = _migratorConnectionString;
        psi.Environment["ConnectionStrings__Configuration"] = _migratorConnectionString;
        psi.Environment["ConnectionStrings__Housekeeping"] = _migratorConnectionString;
        psi.Environment["ConnectionStrings__Dashboard"] = _migratorConnectionString;
        psi.Environment["ConnectionStrings__Communication"] = _migratorConnectionString;
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
