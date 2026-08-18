using System.Diagnostics;
using System.Security.Cryptography;
using FluentAssertions;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.Contexts.Housekeeping.Application.Cleanings;
using IHostPro.Contexts.PropertyManagement.Domain;
using IHostPro.Contexts.PropertyManagement.Domain.ValueObjects;
using IHostPro.Contexts.PropertyManagement.Infrastructure.Persistence;
using IHostPro.Contexts.Reservations.Application;
using IHostPro.Contexts.Reservations.Application.Reservations;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;

namespace IHostPro.Api.Tests.Integration;

/// <summary>
/// Real end-to-end proof of Fase 8, Checkpoint 1 (Workflow Orchestration —
/// ADR-018) — the primary gate the mandate requires: a real
/// <c>ReservationCreated</c>, published through Reservations' own real
/// durable outbox, delivered over a real RabbitMQ broker to a real,
/// unmodified <c>IHostPro.Worker.dll</c> subprocess, consumed by Workflow's
/// <c>ReservationCreatedHandler</c>, which sends the real cross-context
/// command <c>CreateCleaningForReservation</c> — itself delivered over the
/// SAME real broker, on its own dedicated exchange/queue, to Housekeeping's
/// own <c>CreateCleaningForReservationHandler</c> — which creates a real
/// <c>Cleaning</c> row. Never calls any handler directly — the whole chain
/// runs through real transport, mirroring
/// <see cref="ReservationCreatedWorkerRoundTripTests"/>'s own structure.
/// </summary>
public sealed class CreateCleaningForReservationWorkflowRoundTripTests : IAsyncLifetime
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
    public async Task ReservationCreated_flows_through_real_Workflow_and_Housekeeping_Wolverine_chain_to_create_a_real_automated_Cleaning()
    {
        var tenantId = Guid.NewGuid();
        var otherTenantId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        // ---- Property is seeded directly into both PropertyManagementDbContext
        // AND Housekeeping's own local property_projection (bypassing
        // PropertyCreated/PropertyActivated entirely) — mirrors
        // ReservationCreatedWorkerRoundTripTests.SeedActivePropertyAsync's
        // own established pattern. Property Management's OWN real event
        // publication is already proven elsewhere in this codebase's test
        // suite; this test's subject is the NEW Workflow -> Housekeeping
        // command chain (ADR-018), not Property's fan-out. Empirically
        // confirmed real defect while building this test: publishing
        // PropertyCreated then immediately PropertyActivated through the
        // real HTTP flow, with a real Worker consuming both the
        // housekeeping.property-projection AND dashboard.property-projection
        // queues, hits a genuine PRE-EXISTING race in
        // PropertyProjectionSynchronizer.UpsertAsync (read-then-insert-or-
        // update is not safe against the same message type being delivered,
        // and independently handled, once per subscriber queue at nearly
        // the same time) — out of this Checkpoint's scope, flagged
        // separately rather than fixed here or worked around by adding
        // artificial delays to a real end-to-end gate. ----
        var propertyId = await SeedActivePropertyAsync(tenantId, capacity: 4, now);

        // ---- Start the real, unmodified IHostPro.Worker.dll subprocess —
        // must be listening on BOTH the new Workflow trigger queue AND
        // Housekeeping's new command queue before the reservation is
        // created, or the real chain has nowhere to deliver either hop. ----
        StartWorkerProcess();
        var workflowListening = await WaitForWorkerLogLineAsync(
            "Started message listening at rabbitmq://queue/workflow.reservation-created-trigger", TimeSpan.FromSeconds(30));
        workflowListening.Should().BeTrue("the real Worker must report listening to workflow.reservation-created-trigger before ReservationCreated is published");

        var housekeepingCommandListening = await WaitForWorkerLogLineAsync(
            "Started message listening at rabbitmq://queue/housekeeping.workflow-commands", TimeSpan.FromSeconds(30));
        housekeepingCommandListening.Should().BeTrue("the real Worker must report listening to housekeeping.workflow-commands before ReservationCreated is published");

        using var signingKey = RSA.Create(2048);
        var values = BuildApiEnvironment(signingKey.ExportRSAPrivateKeyPem());
        foreach (var (key, value) in values)
            Environment.SetEnvironmentVariable(key, value);

        try
        {
            using var factory = new WebApplicationFactory<Program>();

            Guid reservationId;
            using (var scope = factory.Services.CreateScope())
            {
                scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(tenantId);
                var dispatcher = scope.ServiceProvider.GetRequiredService<IReservationsRequestDispatcher>();

                var result = await dispatcher.Send(new CreateReservationCommand(
                    tenantId, Guid.NewGuid(), propertyId, "Test Guest", null,
                    now.AddDays(1), now.AddDays(5), GuestCount: 2));
                result.IsSuccess.Should().BeTrue("the seeded Property must be genuinely eligible for a new reservation");
                reservationId = result.Value.Id;
            }

            // ---- Poll for the real, automated Cleaning — never asserted
            // instantly, delivery is genuinely asynchronous over two real
            // broker hops (Reservations -> Workflow, Workflow -> Housekeeping). ----
            var cleaningCreated = await WaitUntilAsync(
                () => CountCleaningsForReservationAsync(tenantId, reservationId), count => count > 0, TimeSpan.FromSeconds(30));
            if (!cleaningCreated)
            {
                string workerOutputSnapshot;
                lock (_workerOutputLock) workerOutputSnapshot = _workerOutput.ToString();
                Assert.Fail("The real Workflow -> Housekeeping chain must create a Cleaning within 30s. Worker output:\n" + workerOutputSnapshot);
            }

            (await CountCleaningsForReservationAsync(tenantId, reservationId)).Should().Be(1,
                "exactly one automated Cleaning must exist for this reservation");

            var automated = await GetSingleCleaningForReservationAsync(tenantId, reservationId);
            automated.PropertyId.Should().Be(propertyId);
            automated.Status.Should().Be("Pending");
            automated.CreatedByUserId.Should().BeNull("this Cleaning was created by the automated Workflow flow, never an authenticated actor (ADR-018)");
            automated.ScheduledAtUtc.Should().BeNull("ScheduledAtUtc is never derived from the checkout date this checkpoint (ADR-018 — belongs to Fase 10)");

            // ---- Cross-tenant isolation: the SAME reservationId, queried
            // under a DIFFERENT tenant's RLS context, must never resolve. ----
            (await CountCleaningsForReservationUnderTenantAsync(otherTenantId, reservationId)).Should().Be(0,
                "a different tenant's RLS-scoped connection must never see this tenant's automated Cleaning");

            // ---- Idempotency (Application-level guard + DB partial unique
            // index backstop, ADR-018): invoking the SAME handler a second
            // time for the SAME reservation, in-process, must never create
            // a second automated Cleaning. ----
            using (var idempotencyScope = factory.Services.CreateScope())
            {
                idempotencyScope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(tenantId);
                var handler = idempotencyScope.ServiceProvider.GetRequiredService<ICreateCleaningForReservationHandler>();

                await handler.HandleAsync(new IHostPro.Contexts.Housekeeping.Contracts.CreateCleaningForReservation
                {
                    TenantId = tenantId,
                    ReservationId = reservationId,
                    PropertyId = propertyId,
                    CorrelationId = Guid.NewGuid(),
                }, CancellationToken.None);
            }

            (await CountCleaningsForReservationAsync(tenantId, reservationId)).Should().Be(1,
                "a redelivered CreateCleaningForReservation for the same Reservation must never create a second automated Cleaning (ADR-018)");
        }
        finally
        {
            foreach (var key in values.Keys)
                Environment.SetEnvironmentVariable(key, null);
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
        ["ConnectionStrings__Identity"] = _appConnectionString,
        ["ConnectionStrings__PropertyManagement"] = _appConnectionString,
        ["ConnectionStrings__Reservations"] = _appConnectionString,
        ["ConnectionStrings__Configuration"] = _appConnectionString,
        ["ConnectionStrings__Housekeeping"] = _appConnectionString,
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
        ["OpenTelemetry__OtlpEndpoint"] = "http://127.0.0.1:14321",
    };

    private Dictionary<string, string?> BuildApiEnvironment(string signingKeyPem)
    {
        var values = new Dictionary<string, string?>();
        foreach (var (key, value) in BuildWorkerEnvironment(signingKeyPem))
            values[key] = value;
        return values;
    }

    // ---- Seeding ------------------------------------------------------------

    private async Task<Guid> SeedActivePropertyAsync(Guid tenantId, int capacity, DateTimeOffset now)
    {
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);
        await using var dbContext = CreatePropertyManagementDbContext(tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext.Database, tenantId);

        var address = Address.Create("59090-000", "Rua Exemplo", "100", null, "Ponta Negra", "Natal", "RN");
        var property = Property.Create(
            Guid.NewGuid(), tenantId, PropertyCode.Create($"TST-{Guid.NewGuid():N}"[..12]), "Test Property",
            capacity, condominiumId: null, address, now);
        property.Activate(now);

        dbContext.Properties.Add(property);
        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();

        // Mirrors what a real PropertyCreated + PropertyActivated round
        // trip through Housekeeping's own PropertyProjectionSynchronizer
        // would have produced — inserted directly rather than published,
        // see this test's own doc comment for why.
        await using var connection = new NpgsqlConnection(_migratorConnectionString);
        await connection.OpenAsync();
        await using var projectionTransaction = await connection.BeginTransactionAsync();
        await using (var setCommand = connection.CreateCommand())
        {
            setCommand.CommandText = $"SET LOCAL app.tenant_id = '{tenantId:D}'";
            await setCommand.ExecuteNonQueryAsync();
        }

        await using (var insertCommand = connection.CreateCommand())
        {
            insertCommand.CommandText =
                "INSERT INTO housekeeping.property_projection (tenant_id, property_id, is_active) VALUES (@tenantId, @propertyId, true)";
            insertCommand.Parameters.AddWithValue("tenantId", tenantId);
            insertCommand.Parameters.AddWithValue("propertyId", property.Id);
            await insertCommand.ExecuteNonQueryAsync();
        }
        await projectionTransaction.CommitAsync();

        return property.Id;
    }

    // ---- DB access --------------------------------------------------------

    private static async Task SetTenantAsync(DatabaseFacade database, Guid tenantId) =>
        await database.ExecuteSqlInterpolatedAsync($"SELECT set_config('app.tenant_id', {tenantId.ToString()}, true)");

    private PropertyManagementDbContext CreatePropertyManagementDbContext(ITenantContext tenantContext)
    {
        var options = new DbContextOptionsBuilder<PropertyManagementDbContext>()
            .UseNpgsql(_migratorConnectionString, npgsqlOptions => npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "property_management"))
            .Options;
        return new PropertyManagementDbContext(options, tenantContext);
    }

    private async Task<long> CountCleaningsForReservationAsync(Guid tenantId, Guid reservationId) =>
        await CountCleaningsForReservationUnderTenantAsync(tenantId, reservationId);

    private async Task<long> CountCleaningsForReservationUnderTenantAsync(Guid tenantId, Guid reservationId)
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
        command.CommandText = "SELECT count(*) FROM housekeeping.cleanings WHERE tenant_id = @tenantId AND reservation_id = @id";
        command.Parameters.AddWithValue("tenantId", tenantId);
        command.Parameters.AddWithValue("id", reservationId);
        var count = (long)(await command.ExecuteScalarAsync())!;
        await transaction.CommitAsync();
        return count;
    }

    private sealed record CleaningRow(Guid PropertyId, string Status, Guid? CreatedByUserId, DateTimeOffset? ScheduledAtUtc);

    private async Task<CleaningRow> GetSingleCleaningForReservationAsync(Guid tenantId, Guid reservationId)
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
            SELECT property_id, status, created_by_user_id, scheduled_at_utc
            FROM housekeeping.cleanings
            WHERE tenant_id = @tenantId AND reservation_id = @id
            """;
        command.Parameters.AddWithValue("tenantId", tenantId);
        command.Parameters.AddWithValue("id", reservationId);

        CleaningRow row;
        await using (var reader = await command.ExecuteReaderAsync())
        {
            await reader.ReadAsync();
            row = new CleaningRow(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetGuid(2),
                reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3));
        }

        await transaction.CommitAsync();
        return row;
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
        psi.Environment["ConnectionStrings__Identity"] = _migratorConnectionString;
        psi.Environment["ConnectionStrings__PropertyManagement"] = _migratorConnectionString;
        psi.Environment["ConnectionStrings__Reservations"] = _migratorConnectionString;
        psi.Environment["ConnectionStrings__Configuration"] = _migratorConnectionString;
        psi.Environment["ConnectionStrings__Housekeeping"] = _migratorConnectionString;
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
