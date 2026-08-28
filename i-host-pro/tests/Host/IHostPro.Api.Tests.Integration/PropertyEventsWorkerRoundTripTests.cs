using System.Diagnostics;
using System.Security.Cryptography;
using FluentAssertions;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.Contexts.PropertyManagement.Application;
using IHostPro.Contexts.PropertyManagement.Application.Properties;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;

namespace IHostPro.Api.Tests.Integration;

/// <summary>
/// Real end-to-end proof of Checkpoint 6, gate §6 (ADR-015 generalization —
/// same pattern already proven green for
/// <see cref="ReservationCancelledWorkerRoundTripTests"/>/<see cref="ReservationCreatedWorkerRoundTripTests"/>):
/// each of Property Management's four real, consumed Integration Events
/// (<c>PropertyCreated</c>/<c>PropertyActivated</c>/<c>PropertyDeactivated</c>/
/// <c>PropertyArchived</c>) published through Property Management's own real
/// durable outbox, delivered over a real RabbitMQ broker, consumed by a
/// real, unmodified <c>IHostPro.Worker.dll</c> subprocess through
/// <c>IHousekeepingMessageExecutionScope</c>, one real command dispatch per
/// event, sharing one Property's full real lifecycle within a single
/// Worker/Postgres/RabbitMQ lifecycle (proportional — the same underlying
/// projection write path was already proven correct for Reservations'
/// events; this file is the one, real, broker-driven proof per Property
/// event, not a duplicate investigation).
/// </summary>
public sealed class PropertyEventsWorkerRoundTripTests : IAsyncLifetime
{
    private const string AppRolePassword = "test_app_password";
    private const string MigratorRolePassword = "test_migrator_password";

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

        var (exitCode, output) = await RunMigrationRunnerAsync();
        if (exitCode != 0)
            throw new InvalidOperationException($"MigrationRunner failed with exit code {exitCode}. Output:\n{output}");
    }

    public async Task DisposeAsync()
    {
        if (_workerProcess is { HasExited: false })
        {
            try
            {
                _workerProcess.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already exited between the check and Kill.
            }
            await _workerProcess.WaitForExitAsync();
        }
        _workerProcess?.Dispose();

        await _rabbitMqContainer.DisposeAsync();
        await _postgresContainer.DisposeAsync();
    }

    [Fact]
    public async Task PropertyLifecycleEvents_delivered_through_real_RabbitMQ_to_a_real_Worker_process_keep_the_local_projection_correct_per_event_and_isolated_per_tenant()
    {
        var tenantId = Guid.NewGuid();
        var otherTenantId = Guid.NewGuid();

        StartWorkerProcess();
        var workerReady = await WaitForWorkerLogLineAsync(
            "Started message listening at rabbitmq://queue/housekeeping.property-projection", TimeSpan.FromSeconds(30));
        workerReady.Should().BeTrue("the real Worker must report listening to housekeeping.property-projection before any event is published");

        using var signingKey = RSA.Create(2048);
        var values = BuildApiEnvironment(signingKey.ExportRSAPrivateKeyPem());
        foreach (var (key, value) in values)
            Environment.SetEnvironmentVariable(key, value);

        try
        {
            // ---- One factory for the whole test, but a FRESH DI scope per
            // command dispatch — a real HTTP request never reuses a scope
            // (and therefore never reuses the Scoped MessageContext/
            // IDbContextOutbox<PropertyManagementDbContext> it owns) across
            // multiple requests. Reusing one scope for all four commands was
            // confirmed (via "MessageContext for null has already flushed
            // its outgoing messages" in the API's own log) to silently drop
            // every event after the first — a test-only artifact, not a
            // production defect: PropertyManagement's own transaction
            // executor/handlers are otherwise unchanged and correct. ----
            using var factory = new WebApplicationFactory<Program>();

            Guid propertyId;
            using (var scope = factory.Services.CreateScope())
            {
                scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(tenantId);
                var dispatcher = scope.ServiceProvider.GetRequiredService<IPropertyManagementRequestDispatcher>();

                // ---- PropertyCreated: born Draft — projection row created with IsActive=false ----
                var address = new PropertyAddressInput("59090-000", "Rua Exemplo", "100", null, "Ponta Negra", "Natal", "RN", "BR");
                var createResult = await dispatcher.Send(new CreatePropertyCommand(
                    tenantId, Guid.NewGuid(), $"TST-{Guid.NewGuid():N}"[..12], "Test Property", Capacity: 4,
                    CondominiumId: null, address));
                createResult.IsSuccess.Should().BeTrue("Property creation must succeed with a valid address");
                propertyId = createResult.Value.Id;
            }

            await AssertProjectionEventuallyAsync(tenantId, propertyId, isActive: false,
                "PropertyCreated must create the local projection row with IsActive=false (never Active at creation)");

            // ---- PropertyActivated ----
            using (var scope = factory.Services.CreateScope())
            {
                scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(tenantId);
                var dispatcher = scope.ServiceProvider.GetRequiredService<IPropertyManagementRequestDispatcher>();
                var activateResult = await dispatcher.Send(new ActivatePropertyCommand(tenantId, Guid.NewGuid(), propertyId));
                activateResult.IsSuccess.Should().BeTrue("a Draft property must be genuinely activatable");
            }
            await AssertProjectionEventuallyAsync(tenantId, propertyId, isActive: true,
                "PropertyActivated must set the local projection's IsActive to true");

            // ---- PropertyDeactivated ----
            using (var scope = factory.Services.CreateScope())
            {
                scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(tenantId);
                var dispatcher = scope.ServiceProvider.GetRequiredService<IPropertyManagementRequestDispatcher>();
                var deactivateResult = await dispatcher.Send(new DeactivatePropertyCommand(tenantId, Guid.NewGuid(), propertyId));
                deactivateResult.IsSuccess.Should().BeTrue("an Active property must be genuinely deactivatable");
            }
            await AssertProjectionEventuallyAsync(tenantId, propertyId, isActive: false,
                "PropertyDeactivated must set the local projection's IsActive back to false");

            // ---- PropertyArchived ----
            using (var scope = factory.Services.CreateScope())
            {
                scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(tenantId);
                var dispatcher = scope.ServiceProvider.GetRequiredService<IPropertyManagementRequestDispatcher>();
                var archiveResult = await dispatcher.Send(new ArchivePropertyCommand(tenantId, Guid.NewGuid(), propertyId));
                archiveResult.IsSuccess.Should().BeTrue("an Inactive property must be genuinely archivable");
            }
            await AssertProjectionEventuallyAsync(tenantId, propertyId, isActive: false,
                "PropertyArchived must leave the local projection's IsActive at false (terminal state)");

            // ---- Cross-tenant isolation: the SAME propertyId, queried under
            // a DIFFERENT tenant's RLS context, must never resolve. ----
            (await ProjectionEntryAsync(otherTenantId, propertyId)).Should().BeNull(
                "a different tenant's RLS-scoped connection must never see this tenant's property projection row");
        }
        finally
        {
            foreach (var key in values.Keys)
                Environment.SetEnvironmentVariable(key, null);
        }
    }

    // ---- Assertions -------------------------------------------------------

    private async Task AssertProjectionEventuallyAsync(Guid tenantId, Guid propertyId, bool isActive, string because)
    {
        var matched = await WaitUntilAsync(
            () => ProjectionEntryAsync(tenantId, propertyId),
            entry => entry == isActive,
            TimeSpan.FromSeconds(30));

        if (!matched)
        {
            string workerOutputSnapshot;
            lock (_workerOutputLock) workerOutputSnapshot = _workerOutput.ToString();
            var current = await ProjectionEntryAsync(tenantId, propertyId);
            Assert.Fail($"{because}. Current projection state: {(current is null ? "(no row)" : current.Value.ToString())}. Worker output:\n{workerOutputSnapshot}");
        }
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
        ["ConnectionStrings__Payments"] = _appConnectionString,
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
        ["Configuration__PolicyCache__ConnectionString"] = "localhost:6379",
        ["RabbitMq__Host"] = _rabbitMqContainer.Hostname,
        ["RabbitMq__VirtualHost"] = "/",
        ["RabbitMq__Username"] = RabbitMqBuilder.DefaultUsername,
        ["RabbitMq__Password"] = RabbitMqBuilder.DefaultPassword,
        ["OpenTelemetry__OtlpEndpoint"] = "http://127.0.0.1:14322",
    };

    private static readonly string[] ApiEnvironmentKeys =
    [
        "ConnectionStrings__Identity", "ConnectionStrings__PropertyManagement", "ConnectionStrings__Reservations",
        "ConnectionStrings__Configuration", "ConnectionStrings__Housekeeping", "ConnectionStrings__Platform",
        "Identity__Jwt__Issuer", "Identity__Jwt__Audience", "Identity__Jwt__AccessTokenLifetime", "Identity__Jwt__ClockSkew",
        "Identity__Jwt__SigningKey__PrivateKeyPem",
        "Identity__AccountLockout__MaxFailedAccessAttempts", "Identity__AccountLockout__DefaultLockoutDuration", "Identity__AccountLockout__AllowedForNewUsers",
        "Identity__RefreshToken__Lifetime", "Identity__RefreshToken__SecretSizeBytes", "Identity__RefreshToken__ConcurrentRotationGraceWindow",
        "RabbitMq__Host", "RabbitMq__VirtualHost", "RabbitMq__Username", "RabbitMq__Password",
        "OpenTelemetry__OtlpEndpoint", "ASPNETCORE_ENVIRONMENT",
    ];

    private Dictionary<string, string?> BuildApiEnvironment(string signingKeyPem)
    {
        var values = new Dictionary<string, string?>();
        foreach (var (key, value) in BuildWorkerEnvironment(signingKeyPem))
            values[key] = value;
        return values;
    }

    // ---- DB access --------------------------------------------------------

    private async Task<bool?> ProjectionEntryAsync(Guid tenantId, Guid propertyId)
    {
        await using var connection = new NpgsqlConnection(_migratorConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var setCommand = connection.CreateCommand())
        {
            setCommand.CommandText = $"SET LOCAL app.tenant_id = '{tenantId:D}'";
            await setCommand.ExecuteNonQueryAsync();
        }

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT is_active FROM housekeeping.property_projection WHERE tenant_id = @tenantId AND property_id = @propertyId";
        command.Parameters.AddWithValue("tenantId", tenantId);
        command.Parameters.AddWithValue("propertyId", propertyId);
        var result = await command.ExecuteScalarAsync();
        await transaction.CommitAsync();
        return result is null ? null : ((bool)result);
    }

    private static async Task<bool> WaitUntilAsync<T>(Func<Task<T>> getValue, Func<T, bool> isDone, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (isDone(await getValue()))
                return true;
            await Task.Delay(TimeSpan.FromMilliseconds(300));
        }
        return isDone(await getValue());
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
        psi.Environment["ConnectionStrings__GuestOperations"] = _migratorConnectionString;
        psi.Environment["ConnectionStrings__Payments"] = _migratorConnectionString;
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
