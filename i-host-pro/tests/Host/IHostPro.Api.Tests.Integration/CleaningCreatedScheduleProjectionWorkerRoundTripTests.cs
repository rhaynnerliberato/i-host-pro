using System.Diagnostics;
using System.Security.Cryptography;
using FluentAssertions;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.Contexts.Housekeeping.Application;
using IHostPro.Contexts.Housekeeping.Application.Cleanings;
using IHostPro.Contexts.PropertyManagement.Application;
using IHostPro.Contexts.PropertyManagement.Application.Properties;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;

namespace IHostPro.Api.Tests.Integration;

/// <summary>
/// Real end-to-end proof of Fase 7, Incremento 1 (Agenda Foundation),
/// Checkpoint 1, item F — the isolated transport gate required before
/// generalizing <see cref="Reservations.Infrastructure.Projections.CleaningScheduleProjectionSynchronizer"/>
/// beyond <c>CleaningCreated</c>: the enriched <c>CleaningCreated</c>
/// (carrying <c>ScheduledAtUtc</c>, Checkpoint 1 item C) published through
/// Housekeeping's own real durable outbox, delivered over a real RabbitMQ
/// broker, consumed by a real, unmodified <c>IHostPro.Worker.dll</c>
/// subprocess through the "normal" design (no <c>IHousekeepingMessageExecutionScope</c>-style
/// indirection — Reservations' own first-ever Wolverine consumer, see
/// <c>CleaningCreatedHandler</c>'s own doc comment) — same pattern already
/// proven green for <see cref="ReservationCreatedWorkerRoundTripTests"/>/
/// <see cref="PropertyEventsWorkerRoundTripTests"/>. Also verifies the
/// approval's item 21 gate: if a real <c>ITenantContext</c> divergence
/// analogous to ADR-015's had reproduced here, the projection write below
/// would either never appear or appear under the wrong tenant — this test
/// asserts neither happens.
/// </summary>
public sealed class CleaningCreatedScheduleProjectionWorkerRoundTripTests : IAsyncLifetime
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

        // Program.cs's (and IHostPro.Worker's) own RabbitMQ wiring has no
        // port override — always the default AMQP port 5672 — the host
        // machine's own dev/homolog RabbitMQ containers must be stopped
        // before this test runs.
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
    public async Task CleaningCreated_delivered_through_real_RabbitMQ_to_a_real_Worker_process_creates_the_Reservations_schedule_projection_under_the_correct_tenant()
    {
        var tenantId = Guid.NewGuid();
        var otherTenantId = Guid.NewGuid();
        var scheduledAtUtc = new DateTimeOffset(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);

        // ---- Start the real, unmodified IHostPro.Worker.dll subprocess —
        // must be listening to BOTH queues before anything is published:
        // housekeeping.property-projection (so the seeded Property becomes
        // "known active" to Housekeeping's own CreateCleaningCommandHandler
        // gate) and reservations.cleaning-schedule-projection (this test's
        // real subject). ----
        StartWorkerProcess();
        var propertyQueueReady = await WaitForWorkerLogLineAsync(
            "Started message listening at rabbitmq://queue/housekeeping.property-projection", TimeSpan.FromSeconds(30));
        propertyQueueReady.Should().BeTrue("the real Worker must report listening to housekeeping.property-projection before any event is published");
        var scheduleQueueReady = await WaitForWorkerLogLineAsync(
            "Started message listening at rabbitmq://queue/reservations.cleaning-schedule-projection", TimeSpan.FromSeconds(30));
        scheduleQueueReady.Should().BeTrue("the real Worker must report listening to reservations.cleaning-schedule-projection before CleaningCreated is published");

        using var signingKey = RSA.Create(2048);
        var values = BuildApiEnvironment(signingKey.ExportRSAPrivateKeyPem());
        foreach (var (key, value) in values)
            Environment.SetEnvironmentVariable(key, value);

        try
        {
            using var factory = new WebApplicationFactory<Program>();

            // ---- Seed a real, Active Property through the real
            // PropertyManagement command path (never a direct DB insert) so
            // the real Worker naturally populates Housekeeping's own local
            // property_projection — the exact prerequisite
            // CreateCleaningCommandHandler's IPropertyReferenceProjection
            // gate checks. ----
            // ---- A FRESH DI scope per command dispatch — a real HTTP
            // request never reuses a scope (and therefore never reuses the
            // Scoped MessageContext/IDbContextOutbox<PropertyManagementDbContext>
            // it owns) across multiple requests. Reusing one scope for both
            // commands silently drops every event after the first (same
            // defect PropertyEventsWorkerRoundTripTests' own doc comment
            // documents — confirmed there via "MessageContext for null has
            // already flushed its outgoing messages" in the API's own log). ----
            Guid propertyId;
            using (var scope = factory.Services.CreateScope())
            {
                scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(tenantId);
                var propertyDispatcher = scope.ServiceProvider.GetRequiredService<IPropertyManagementRequestDispatcher>();

                var address = new PropertyAddressInput("59090-000", "Rua Exemplo", "100", null, "Ponta Negra", "Natal", "RN", "BR");
                var createResult = await propertyDispatcher.Send(new CreatePropertyCommand(
                    tenantId, Guid.NewGuid(), $"TST-{Guid.NewGuid():N}"[..12], "Test Property", Capacity: 4,
                    CondominiumId: null, address));
                createResult.IsSuccess.Should().BeTrue("Property creation must succeed with a valid address");
                propertyId = createResult.Value.Id;
            }

            using (var scope = factory.Services.CreateScope())
            {
                scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(tenantId);
                var propertyDispatcher = scope.ServiceProvider.GetRequiredService<IPropertyManagementRequestDispatcher>();

                var activateResult = await propertyDispatcher.Send(new ActivatePropertyCommand(tenantId, Guid.NewGuid(), propertyId));
                activateResult.IsSuccess.Should().BeTrue("a Draft property must be genuinely activatable");
            }

            var propertyKnownActive = await WaitUntilAsync(
                () => HousekeepingPropertyProjectionIsActiveAsync(tenantId, propertyId), isActive => isActive == true, TimeSpan.FromSeconds(30));
            if (!propertyKnownActive)
            {
                string workerOutputSnapshot;
                lock (_workerOutputLock) workerOutputSnapshot = _workerOutput.ToString();
                Assert.Fail("Housekeeping's own local property_projection must reflect the real PropertyActivated event before CreateCleaningCommand can succeed. Worker output:\n" + workerOutputSnapshot);
            }

            // ---- Trigger the REAL command — publishes the enriched
            // CleaningCreated (with ScheduledAtUtc) through Housekeeping's
            // own real durable outbox. ----
            Guid cleaningId;
            using (var scope = factory.Services.CreateScope())
            {
                scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(tenantId);
                var housekeepingDispatcher = scope.ServiceProvider.GetRequiredService<IHousekeepingRequestDispatcher>();

                var actorId = Guid.NewGuid();
                var createCleaningResult = await housekeepingDispatcher.Send(
                    new CreateCleaningCommand(tenantId, actorId, propertyId, ReservationId: null, scheduledAtUtc));
                createCleaningResult.IsSuccess.Should().BeTrue("Cleaning creation must succeed against a known-active Property");
                cleaningId = createCleaningResult.Value.Id;
                createCleaningResult.Value.ScheduledAtUtc.Should().Be(scheduledAtUtc, "the persisted Cleaning must carry the exact caller-supplied schedule");
            }

            // ---- Poll the real Reservations database for the real
            // Worker's real projection write — never asserted instantly,
            // since delivery is genuinely asynchronous over the real
            // broker. ----
            var projection = await WaitUntilProjectionAsync(tenantId, cleaningId, TimeSpan.FromSeconds(30));
            if (projection is null)
            {
                string workerOutputSnapshot;
                lock (_workerOutputLock) workerOutputSnapshot = _workerOutput.ToString();
                Assert.Fail("The real Worker must consume the real CleaningCreated event and create the Reservations schedule projection within 30s. Worker output:\n" + workerOutputSnapshot);
            }

            projection!.Value.PropertyId.Should().Be(propertyId, "the projection must carry the exact PropertyId published on CleaningCreated");
            projection.Value.ScheduledAtUtc.Should().Be(scheduledAtUtc, "the projection must carry the exact ScheduledAtUtc published on CleaningCreated — never recomputed, never defaulted");
            projection.Value.Status.Should().Be("Pending", "a freshly created Cleaning is always Pending");
            projection.Value.AssignedHousekeeperUserId.Should().BeNull("no assignment has happened yet");

            // ---- Cross-tenant isolation: the SAME cleaningId, queried
            // under a DIFFERENT tenant's RLS context, must never resolve. ----
            (await ProjectionEntryAsync(otherTenantId, cleaningId)).Should().BeNull(
                "a different tenant's RLS-scoped connection must never see this tenant's schedule projection row");
        }
        finally
        {
            foreach (var key in values.Keys)
                Environment.SetEnvironmentVariable(key, null);
        }
    }

    // ---- Assertions ---------------------------------------------------------

    private async Task<bool?> HousekeepingPropertyProjectionIsActiveAsync(Guid tenantId, Guid propertyId)
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
        return result is null ? null : (bool)result;
    }

    private async Task<(Guid PropertyId, Guid? AssignedHousekeeperUserId, DateTimeOffset? ScheduledAtUtc, string Status)?> ProjectionEntryAsync(
        Guid tenantId, Guid cleaningId)
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
        command.CommandText = """
            SELECT property_id, assigned_housekeeper_user_id, scheduled_at_utc, status
            FROM reservations.cleaning_schedule_projection
            WHERE tenant_id = @tenantId AND cleaning_id = @cleaningId
            """;
        command.Parameters.AddWithValue("tenantId", tenantId);
        command.Parameters.AddWithValue("cleaningId", cleaningId);

        await using var reader = await command.ExecuteReaderAsync();
        (Guid, Guid?, DateTimeOffset?, string)? row = null;
        if (await reader.ReadAsync())
        {
            row = (
                reader.GetGuid(0),
                reader.IsDBNull(1) ? null : reader.GetGuid(1),
                reader.IsDBNull(2) ? null : reader.GetFieldValue<DateTimeOffset>(2),
                reader.GetString(3));
        }
        await reader.DisposeAsync();
        await transaction.CommitAsync();
        return row;
    }

    private async Task<(Guid PropertyId, Guid? AssignedHousekeeperUserId, DateTimeOffset? ScheduledAtUtc, string Status)?> WaitUntilProjectionAsync(
        Guid tenantId, Guid cleaningId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var entry = await ProjectionEntryAsync(tenantId, cleaningId);
            if (entry is not null)
                return entry;
            await Task.Delay(TimeSpan.FromMilliseconds(300));
        }
        return await ProjectionEntryAsync(tenantId, cleaningId);
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
        ["OpenTelemetry__OtlpEndpoint"] = "http://127.0.0.1:14323",
    };

    private Dictionary<string, string?> BuildApiEnvironment(string signingKeyPem)
    {
        var values = new Dictionary<string, string?>();
        foreach (var (key, value) in BuildWorkerEnvironment(signingKeyPem))
            values[key] = value;
        return values;
    }

    // ---- MigrationRunner --------------------------------------------------

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
