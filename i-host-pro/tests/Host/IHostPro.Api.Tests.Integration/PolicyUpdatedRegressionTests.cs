using System.Diagnostics;
using System.Security.Cryptography;
using FluentAssertions;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.Contexts.Configuration.Application;
using IHostPro.Contexts.Configuration.Application.Policies;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using StackExchange.Redis;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;
using Testcontainers.Redis;

namespace IHostPro.Api.Tests.Integration;

/// <summary>
/// Fase 6, Checkpoint 6 homologação, item 13 of the user's approved
/// protocol — ONE focused regression round-trip for <c>PolicyUpdated</c>
/// (Fase 5, already closed), NOT a full Fase 5 re-homologation. Confirms the
/// ADR-015 generalization work in this checkpoint (the global
/// <c>opts.UseEntityFrameworkCoreTransactions()</c> call, the
/// <c>AlwaysUseServiceLocationFor&lt;IHousekeepingMessageExecutionScope&gt;()</c>
/// codegen exception, the two new Housekeeping listeners sharing the same
/// Worker process) caused zero regression to Configuration &amp; Policy's own
/// consumer:
///
///   (a) consumer still active / exactly one handler / no codegen error —
///       already proven by <c>HousekeepingWolverineDiscoveryTests</c>, which
///       boots the SAME real Worker process this test also exercises and
///       asserts exactly one "Started message listening" line for
///       <c>configuration.policy-updated</c> with zero known failure
///       signatures;
///   (b) zero dependency on the new execution boundary — confirmed by
///       reading <c>PolicyUpdatedCacheInvalidation</c> (depends only on
///       <c>IPolicyCacheInvalidator</c>) and already enforced continuously by
///       <c>ConfigurationDependencyTests</c>, which asserts every
///       Configuration &amp; Policy assembly never depends on any other
///       Bounded Context at all;
///   (c) cache generation actually advances — the one functional fact
///       neither of the above proves, and what THIS test exists to confirm:
///       real RabbitMQ, real Worker, real Redis.
/// </summary>
public sealed class PolicyUpdatedRegressionTests : IAsyncLifetime
{
    private const string AppRolePassword = "test_app_password";
    private const string MigratorRolePassword = "test_migrator_password";

    private PostgreSqlContainer _postgresContainer = null!;
    private RabbitMqContainer _rabbitMqContainer = null!;
    private RedisContainer _redisContainer = null!;
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
        _redisContainer = new RedisBuilder().WithImage("redis:7-alpine").Build();
        await Task.WhenAll(_postgresContainer.StartAsync(), _redisContainer.StartAsync());

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

        var (exitCode, output) = await RunMigrationRunnerAsync();
        if (exitCode != 0)
            throw new InvalidOperationException($"MigrationRunner failed with exit code {exitCode}. Output:\n{output}");
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
        await Task.WhenAll(_postgresContainer.DisposeAsync().AsTask(), _redisContainer.DisposeAsync().AsTask());
    }

    [Fact]
    public async Task PolicyUpdated_delivered_through_real_RabbitMQ_to_the_real_Worker_advances_the_real_Redis_cache_generation()
    {
        var tenantId = Guid.NewGuid();

        StartWorkerProcess();
        var workerReady = await WaitForWorkerLogLineAsync(
            "Started message listening at rabbitmq://queue/configuration.policy-updated", TimeSpan.FromSeconds(30));
        workerReady.Should().BeTrue("the real Worker must report listening to configuration.policy-updated before the event is published");

        var generationKey = (RedisKey)$"ihostpro:{tenantId:N}:policy-cache:EARLY_CHECKIN:gen";
        await using var redisConnection = await ConnectionMultiplexer.ConnectAsync(_redisContainer.GetConnectionString());
        var redisDatabase = redisConnection.GetDatabase();
        var generationBefore = await redisDatabase.StringGetAsync(generationKey);
        generationBefore.IsNullOrEmpty.Should().BeTrue("no PolicyUpdated has been published yet for this fresh tenant/policy pair");

        using var signingKey = RSA.Create(2048);
        var values = BuildApiEnvironment(signingKey.ExportRSAPrivateKeyPem());
        foreach (var (key, value) in values)
            Environment.SetEnvironmentVariable(key, value);

        try
        {
            using var factory = new WebApplicationFactory<Program>();
            using var scope = factory.Services.CreateScope();
            scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(tenantId);
            var dispatcher = scope.ServiceProvider.GetRequiredService<IConfigurationRequestDispatcher>();

            const string value = """{"allowed":true,"requiresCleaningCompleted":false,"requiresForm":false,"notifyFrontDesk":false}""";
            var result = await dispatcher.Send(
                new CreatePolicyValueVersionCommand(tenantId, Guid.NewGuid(), "EARLY_CHECKIN", "Tenant", null, value, "Checkpoint 6 regression check", null, null),
                CancellationToken.None);
            result.IsSuccess.Should().BeTrue("creating a new policy value version must succeed and publish a real PolicyUpdated");
        }
        finally
        {
            foreach (var key in values.Keys)
                Environment.SetEnvironmentVariable(key, null);
        }

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        RedisValue generationAfter = RedisValue.Null;
        while (DateTime.UtcNow < deadline)
        {
            generationAfter = await redisDatabase.StringGetAsync(generationKey);
            if (!generationAfter.IsNullOrEmpty) break;
            await Task.Delay(TimeSpan.FromMilliseconds(300));
        }

        generationAfter.IsNullOrEmpty.Should().BeFalse(
            "the real Worker must consume the real PolicyUpdated and call IPolicyCacheInvalidator.InvalidateAsync, advancing the Redis generation counter — no regression from this checkpoint's ADR-015 changes");
        ((long)generationAfter).Should().Be(1, "exactly one InvalidateAsync (INCR) must have run for this single real PolicyUpdated");
    }

    // ---- Worker subprocess ----------------------------------------------

    private readonly System.Text.StringBuilder _workerOutput = new();
    private readonly object _workerOutputLock = new();
    private readonly List<TaskCompletionSource<bool>> _workerLineWaiters = [];
    private readonly List<string> _workerLineWaiterPatterns = [];

    private void StartWorkerProcess()
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
        foreach (var (key, value) in BuildWorkerEnvironment(signingKey.ExportRSAPrivateKeyPem()))
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

    private Dictionary<string, string?> BuildWorkerEnvironment(string signingKeyPem) => new()
    {
        ["ASPNETCORE_ENVIRONMENT"] = "Development",
        ["DOTNET_ENVIRONMENT"] = "Development",
        ["ConnectionStrings__Identity"] = _appConnectionString,
        ["ConnectionStrings__PropertyManagement"] = _appConnectionString,
        ["ConnectionStrings__Reservations"] = _appConnectionString,
        ["ConnectionStrings__Configuration"] = _appConnectionString,
        ["ConnectionStrings__Housekeeping"] = _appConnectionString,
        ["ConnectionStrings__Communication"] = _appConnectionString,
        ["ConnectionStrings__GuestOperations"] = _appConnectionString,
        ["ConnectionStrings__ExternalIntegrations"] = _appConnectionString,
        ["ConnectionStrings__Dashboard"] = _appConnectionString,
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
        ["Configuration__PolicyCache__ConnectionString"] = _redisContainer.GetConnectionString(),
        ["RabbitMq__Host"] = _rabbitMqContainer.Hostname,
        ["RabbitMq__VirtualHost"] = "/",
        ["RabbitMq__Username"] = RabbitMqBuilder.DefaultUsername,
        ["RabbitMq__Password"] = RabbitMqBuilder.DefaultPassword,
        ["OpenTelemetry__OtlpEndpoint"] = "http://127.0.0.1:14325",
    };

    private Dictionary<string, string?> BuildApiEnvironment(string signingKeyPem) => BuildWorkerEnvironment(signingKeyPem);

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
        psi.Environment["ConnectionStrings__GuestOperations"] = _migratorConnectionString;
        psi.Environment["ConnectionStrings__ExternalIntegrations"] = _migratorConnectionString;
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
