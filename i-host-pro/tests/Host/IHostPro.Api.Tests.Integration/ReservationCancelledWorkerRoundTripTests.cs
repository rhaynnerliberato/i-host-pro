using System.Diagnostics;
using System.Security.Cryptography;
using FluentAssertions;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.Contexts.Housekeeping.Domain;
using IHostPro.Contexts.Housekeeping.Domain.Enums;
using IHostPro.Contexts.Housekeeping.Infrastructure.Persistence;
using IHostPro.Contexts.Reservations.Application;
using IHostPro.Contexts.Reservations.Application.Reservations;
using IHostPro.Contexts.Reservations.Domain;
using IHostPro.Contexts.Reservations.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;

namespace IHostPro.Api.Tests.Integration;

/// <summary>
/// Real end-to-end proof of Checkpoint 6, gate §6:
/// <c>ReservationCancelled</c> published through Reservations' own real
/// durable outbox, delivered over a real RabbitMQ broker, consumed by a
/// real, unmodified <c>IHostPro.Worker.dll</c> subprocess (never the
/// <c>ReservationProjectionAndCancellationReaction</c> handler invoked
/// directly, which would only prove the handler's own logic, not the
/// transport). The cancelling command itself is dispatched directly via
/// <see cref="IReservationsRequestDispatcher"/> (the exact same call
/// <c>ReservationsController.CancelReservation</c> makes) — a real command
/// execution through the real outbox, just without the HTTP/JWT layer,
/// which this test has no need to exercise.
/// </summary>
public sealed class ReservationCancelledWorkerRoundTripTests : IAsyncLifetime
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
    public async Task ReservationCancelled_delivered_through_real_RabbitMQ_to_a_real_Worker_process_auto_cancels_the_linked_Pending_cleaning_and_never_touches_a_Completed_one_or_another_tenants_cleaning()
    {
        var tenantId = Guid.NewGuid();
        var otherTenantId = Guid.NewGuid();
        var propertyId = Guid.NewGuid();
        var reservationId = Guid.NewGuid();
        var otherReservationId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        // ---- Seed real aggregates directly (never the HTTP creation path
        // — this test's subject is ReservationCancelled's real delivery and
        // reaction, not reservation/cleaning creation, both already covered
        // elsewhere) ----
        await SeedReservationAsync(tenantId, reservationId, propertyId, now);
        var pendingCleaningId = await SeedCleaningAsync(tenantId, propertyId, reservationId, CleaningStatus.Pending, now);
        var completedCleaningId = await SeedCompletedCleaningAsync(tenantId, propertyId, reservationId, now);

        await SeedReservationAsync(otherTenantId, otherReservationId, Guid.NewGuid(), now);
        var otherTenantCleaningId = await SeedCleaningAsync(otherTenantId, Guid.NewGuid(), otherReservationId, CleaningStatus.Pending, now);

        // ---- Start the real, unmodified IHostPro.Worker.dll subprocess —
        // the actual consumer of housekeeping.reservation-projection ----
        StartWorkerProcess();
        var workerReady = await WaitForWorkerLogLineAsync(
            "Started message listening at rabbitmq://queue/housekeeping.reservation-projection", TimeSpan.FromSeconds(30));
        workerReady.Should().BeTrue("the real Worker must report listening to housekeeping.reservation-projection before the event is published");

        // ---- Probe queue for CleaningCancelled, bound BEFORE the command
        // dispatches — declared here (not after cancellation is confirmed)
        // specifically to avoid the exact race documented in
        // WolverineThreeStoreCompositionTests: Wolverine's own outbox flush
        // can synchronously deliver-and-clear the durable-outbox row before
        // any later query against wolverine_outgoing_envelopes runs, making
        // "0 rows" ambiguous between "never sent" and "already delivered".
        // A bound queue observes the actual delivery directly. ----
        await using var cleaningCancelledProbe = await DeclareCleaningCancelledProbeQueueAsync();

        // ---- Trigger the REAL command — publishes ReservationCancelled
        // through Reservations' own real durable outbox ----
        using var signingKey = RSA.Create(2048);
        var values = BuildApiEnvironment(signingKey.ExportRSAPrivateKeyPem());
        foreach (var (key, value) in values)
            Environment.SetEnvironmentVariable(key, value);

        try
        {
            using var factory = new WebApplicationFactory<Program>();
            using var scope = factory.Services.CreateScope();
            scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(tenantId);
            var dispatcher = scope.ServiceProvider.GetRequiredService<IReservationsRequestDispatcher>();

            var result = await dispatcher.Send(new CancelReservationCommand(tenantId, Guid.NewGuid(), reservationId));
            result.IsSuccess.Should().BeTrue("the seeded reservation must be genuinely cancellable");
        }
        finally
        {
            foreach (var key in values.Keys)
                Environment.SetEnvironmentVariable(key, null);
        }

        // ---- Poll the real Housekeeping database for the real Worker's
        // real reaction — never asserted instantly, since delivery is
        // genuinely asynchronous over the real broker ----
        var pendingCancelled = await WaitUntilAsync(
            () => GetCleaningStatusAsync(tenantId, pendingCleaningId), status => status == "Cancelled", TimeSpan.FromSeconds(30));
        if (!pendingCancelled)
        {
            string workerOutputSnapshot;
            lock (_workerOutputLock) workerOutputSnapshot = _workerOutput.ToString();
            Assert.Fail("The real Worker must consume the real ReservationCancelled event and cancel the linked Pending cleaning within 30s. Worker output:\n" + workerOutputSnapshot);
        }

        (await GetCleaningStatusAsync(tenantId, completedCleaningId)).Should().Be("Completed", "a Completed cleaning must never be touched by the reaction");
        (await GetCleaningStatusAsync(otherTenantId, otherTenantCleaningId)).Should().Be("Pending", "a different tenant's cleaning must never be touched");

        var auditCount = await CountCleaningCancelledByReservationAuditEntriesAsync(tenantId, pendingCleaningId);
        auditCount.Should().Be(1, "exactly one cleaning_cancelled_by_reservation_cancellation audit entry must exist — no duplication");

        // ---- Real broker delivery, via the queue bound before dispatch —
        // definitive proof the outbox actually routed CleaningCancelled onto
        // housekeeping-events, not merely staged-then-cleared. ----
        RabbitMQ.Client.BasicGetResult? delivered = null;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            delivered = await cleaningCancelledProbe.Channel.BasicGetAsync(cleaningCancelledProbe.Queue, autoAck: false);
            if (delivered is not null) break;
            await Task.Delay(TimeSpan.FromMilliseconds(300));
        }
        delivered.Should().NotBeNull("exactly one CleaningCancelled must be delivered onto housekeeping-events/cleaning_cancelled");
        delivered!.RoutingKey.Should().Be("cleaning_cancelled");
        await cleaningCancelledProbe.Channel.BasicAckAsync(delivered.DeliveryTag, multiple: false);

        var secondMessage = await cleaningCancelledProbe.Channel.BasicGetAsync(cleaningCancelledProbe.Queue, autoAck: true);
        secondMessage.Should().BeNull("no duplicate CleaningCancelled may ever be published for a single real transition");
    }

    // ---- Seeding ------------------------------------------------------------

    private async Task SeedReservationAsync(Guid tenantId, Guid reservationId, Guid propertyId, DateTimeOffset now)
    {
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);
        await using var dbContext = CreateReservationsDbContext(tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext.Database, tenantId);

        var reservation = Reservation.Create(
            reservationId, tenantId, propertyId, "Test Guest", null,
            now.AddDays(1), now.AddDays(5), guestCount: 2, now);
        dbContext.Reservations.Add(reservation);
        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();
    }

    private async Task<Guid> SeedCleaningAsync(Guid tenantId, Guid propertyId, Guid reservationId, CleaningStatus status, DateTimeOffset now)
    {
        var cleaning = Cleaning.Create(Guid.NewGuid(), tenantId, propertyId, reservationId, Guid.NewGuid(), now);
        if (status is CleaningStatus.Assigned or CleaningStatus.Started or CleaningStatus.InInspection or CleaningStatus.Completed)
            cleaning.Assign(Guid.NewGuid(), now);
        if (status is CleaningStatus.Started or CleaningStatus.InInspection or CleaningStatus.Completed)
            cleaning.Start(now);
        if (status is CleaningStatus.InInspection or CleaningStatus.Completed)
            cleaning.StartInspection(now);
        if (status is CleaningStatus.Completed)
            cleaning.Complete(now);

        await InsertCleaningAsync(tenantId, cleaning);
        return cleaning.Id;
    }

    private Task<Guid> SeedCompletedCleaningAsync(Guid tenantId, Guid propertyId, Guid reservationId, DateTimeOffset now) =>
        SeedCleaningAsync(tenantId, propertyId, reservationId, CleaningStatus.Completed, now);

    private async Task InsertCleaningAsync(Guid tenantId, Cleaning cleaning)
    {
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);
        await using var dbContext = CreateHousekeepingDbContext(tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext.Database, tenantId);

        dbContext.Cleanings.Add(cleaning);
        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();
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
        ["OpenTelemetry__OtlpEndpoint"] = "http://127.0.0.1:14320",
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

    private static async Task SetTenantAsync(DatabaseFacade database, Guid tenantId) =>
        await database.ExecuteSqlInterpolatedAsync($"SELECT set_config('app.tenant_id', {tenantId.ToString()}, true)");

    private ReservationsDbContext CreateReservationsDbContext(ITenantContext tenantContext)
    {
        var options = new DbContextOptionsBuilder<ReservationsDbContext>()
            .UseNpgsql(_migratorConnectionString, npgsqlOptions => npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "reservations"))
            .Options;
        return new ReservationsDbContext(options, tenantContext);
    }

    private HousekeepingDbContext CreateHousekeepingDbContext(ITenantContext tenantContext)
    {
        var options = new DbContextOptionsBuilder<HousekeepingDbContext>()
            .UseNpgsql(_migratorConnectionString, npgsqlOptions => npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "housekeeping"))
            .Options;
        return new HousekeepingDbContext(options, tenantContext);
    }

    private async Task<string?> GetCleaningStatusAsync(Guid tenantId, Guid cleaningId)
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
        command.CommandText = "SELECT status FROM housekeeping.cleanings WHERE id = @id";
        command.Parameters.AddWithValue("id", cleaningId);
        var result = await command.ExecuteScalarAsync();
        await transaction.CommitAsync();
        return (string?)result;
    }

    private async Task<long> CountCleaningCancelledByReservationAuditEntriesAsync(Guid tenantId, Guid cleaningId)
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
        command.CommandText = "SELECT count(*) FROM housekeeping.cleaning_audit_log WHERE action_code = 'cleaning_cancelled_by_reservation_cancellation' AND aggregate_id = @id";
        command.Parameters.AddWithValue("id", cleaningId);
        var count = (long)(await command.ExecuteScalarAsync())!;
        await transaction.CommitAsync();
        return count;
    }

    /// <summary>
    /// Test-only diagnostic infrastructure: an exclusive, auto-delete queue
    /// bound directly via <c>RabbitMQ.Client</c> (never Wolverine, never
    /// added to any production Host/MigrationRunner) to the
    /// housekeeping-events exchange MigrationRunner already provisions —
    /// mirrors <c>WolverineThreeStoreCompositionTests</c>' own established
    /// probe-queue pattern. Declared BEFORE the cancel command dispatches,
    /// specifically to avoid the race where Wolverine's own outbox flush can
    /// synchronously deliver-and-clear the durable-outbox row before a
    /// later query against <c>wolverine_outgoing_envelopes</c> runs — a
    /// bound queue observes the actual delivery directly instead.
    /// </summary>
    private sealed class CleaningCancelledProbe : IAsyncDisposable
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

    private async Task<CleaningCancelledProbe> DeclareCleaningCancelledProbeQueueAsync()
    {
        var connectionFactory = new RabbitMQ.Client.ConnectionFactory
        {
            HostName = _rabbitMqContainer.Hostname,
            UserName = RabbitMqBuilder.DefaultUsername,
            Password = RabbitMqBuilder.DefaultPassword,
            VirtualHost = "/",
        };

        var connection = await connectionFactory.CreateConnectionAsync();
        var channel = await connection.CreateChannelAsync();

        var queue = $"test-cleaning-cancelled-probe-{Guid.NewGuid():N}";
        await channel.QueueDeclareAsync(queue, durable: false, exclusive: true, autoDelete: true);
        await channel.QueueBindAsync(queue, "housekeeping-events", "cleaning_cancelled");

        return new CleaningCancelledProbe { Connection = connection, Channel = channel, Queue = queue };
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
