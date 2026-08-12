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
using RabbitMQ.Client;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;

namespace IHostPro.Api.Tests.Integration;

/// <summary>
/// Fase 6, Checkpoint 6 homologação — real redelivery proof required by the
/// user's own approved protocol: not "domain seems idempotent" (inferred
/// from code reading) but an actually-observed redelivery of the SAME
/// Wolverine envelope after it was already successfully processed once.
///
/// Technique: <c>reservation-events</c> is a topic exchange — binding a
/// second, test-only probe queue to the same <c>reservation_cancelled</c>
/// routing key the real <c>housekeeping.reservation-projection</c> queue is
/// bound to means both queues receive an IDENTICAL, unmodified copy of
/// whatever Wolverine actually put on the wire (same body bytes, same AMQP
/// properties, same headers, including <c>Envelope.Id</c>). After the real Worker finishes
/// processing that first delivery successfully, this test takes the probe's
/// captured copy and re-publishes those EXACT bytes/properties directly onto
/// the real queue via the RabbitMQ default exchange — a genuine second
/// delivery of the same message, not a synthetic one, and never touching any
/// Wolverine private/internal type.
/// </summary>
public sealed class ReservationCancelledRedeliveryTests : IAsyncLifetime
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
    public async Task ReservationCancelled_real_redelivery_of_the_identical_wire_envelope_after_successful_processing_does_not_duplicate_effects()
    {
        var tenantId = Guid.NewGuid();
        var propertyId = Guid.NewGuid();
        var reservationId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        await SeedReservationAsync(tenantId, reservationId, propertyId, now);
        var cleaningId = await SeedPendingCleaningAsync(tenantId, propertyId, reservationId, now);

        StartWorkerProcess();
        var workerReady = await WaitForWorkerLogLineAsync(
            "Started message listening at rabbitmq://queue/housekeeping.reservation-projection", TimeSpan.FromSeconds(30));
        workerReady.Should().BeTrue("the real Worker must report listening to housekeeping.reservation-projection before the event is published");

        // Two independent bindings to the SAME real routing key, declared
        // BEFORE dispatch — the real queue (already provisioned by
        // MigrationRunner) and this test's own raw-capture probe. Both
        // receive an identical copy of whatever gets published.
        await using var envelopeProbe = await DeclareReservationCancelledProbeQueueAsync();
        await using var cleaningCancelledProbe = await DeclareCleaningCancelledProbeQueueAsync();

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

        // ---- First (real, unique) delivery: must fully succeed ----
        var firstProcessed = await WaitUntilAsync(
            () => GetCleaningStatusAsync(tenantId, cleaningId), status => status == "Cancelled", TimeSpan.FromSeconds(30));
        firstProcessed.Should().BeTrue("the first, unique delivery of ReservationCancelled must be fully processed before redelivery is attempted");

        var auditCountAfterFirstDelivery = await CountCleaningCancelledAuditEntriesAsync(tenantId, cleaningId);
        auditCountAfterFirstDelivery.Should().Be(1, "the first delivery must produce exactly one audit entry");

        var firstCleaningCancelled = await BasicGetWithRetryAsync(cleaningCancelledProbe.Channel, cleaningCancelledProbe.Queue, TimeSpan.FromSeconds(15));
        firstCleaningCancelled.Should().NotBeNull("the first delivery must publish exactly one CleaningCancelled");
        await cleaningCancelledProbe.Channel.BasicAckAsync(firstCleaningCancelled!.DeliveryTag, multiple: false);

        // ---- Capture the exact wire envelope the real queue also received ----
        var captured = await BasicGetWithRetryAsync(envelopeProbe.Channel, envelopeProbe.Queue, TimeSpan.FromSeconds(15));
        captured.Should().NotBeNull("the probe, bound to the same routing key as the real queue, must have received an identical copy of the published ReservationCancelled");
        await envelopeProbe.Channel.BasicAckAsync(captured!.DeliveryTag, multiple: false);

        string workerOutputBeforeRedelivery;
        lock (_workerOutputLock) workerOutputBeforeRedelivery = _workerOutput.ToString();

        // ---- Real redelivery: the exact same bytes + AMQP properties
        // (including whatever headers Wolverine embedded for its own
        // envelope identity), published a second time directly onto the
        // real queue via the default exchange. A genuine second delivery of
        // the same message — not a synthetic reconstruction. ----
        var redeliveredProperties = new BasicProperties(captured.BasicProperties);
        await envelopeProbe.Channel.BasicPublishAsync(
            exchange: string.Empty,
            routingKey: "housekeeping.reservation-projection",
            mandatory: false,
            basicProperties: redeliveredProperties,
            body: captured.Body,
            cancellationToken: CancellationToken.None);

        // ---- Give the real Worker a real window to (attempt to) process
        // the redelivered envelope, then assert no duplicate effect ----
        await Task.Delay(TimeSpan.FromSeconds(5));

        (await GetCleaningStatusAsync(tenantId, cleaningId)).Should().Be("Cancelled", "redelivery of an already-cancelled Cleaning must never change its terminal state");

        var auditCountAfterRedelivery = await CountCleaningCancelledAuditEntriesAsync(tenantId, cleaningId);
        auditCountAfterRedelivery.Should().Be(1, "redelivery of the same envelope must never produce a second audit entry");

        var secondCleaningCancelled = await cleaningCancelledProbe.Channel.BasicGetAsync(cleaningCancelledProbe.Queue, autoAck: true);
        secondCleaningCancelled.Should().BeNull("redelivery of the same envelope must never publish a second CleaningCancelled");

        string workerOutputAfterRedelivery;
        lock (_workerOutputLock) workerOutputAfterRedelivery = _workerOutput.ToString();
        var newWorkerOutputSinceRedelivery = workerOutputAfterRedelivery.Length > workerOutputBeforeRedelivery.Length
            ? workerOutputAfterRedelivery[workerOutputBeforeRedelivery.Length..]
            : string.Empty;

        // Diagnostic-only: recorded for the Fase 6 documentation narrative
        // (§10.7/§10.8), never asserted on — Wolverine's own internal log
        // wording is not a stable contract to hard-couple a test to. The
        // functional assertions above (final state / audit count /
        // CleaningCancelled count) are what actually prove no duplicate
        // effect occurred — confirmed (§10.8) to be domain-level idempotency
        // alone; this queue has no durable inbox at all (EndpointMode.Inline).
        _ = newWorkerOutputSinceRedelivery;
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

    private async Task<Guid> SeedPendingCleaningAsync(Guid tenantId, Guid propertyId, Guid reservationId, DateTimeOffset now)
    {
        var cleaning = Cleaning.Create(Guid.NewGuid(), tenantId, propertyId, reservationId, Guid.NewGuid(), now);

        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);
        await using var dbContext = CreateHousekeepingDbContext(tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext.Database, tenantId);

        dbContext.Cleanings.Add(cleaning);
        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();

        return cleaning.Id;
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
        // Diagnostic-only for this test: raises Wolverine/EF Core's own
        // Debug-level tracing so a human reviewing the captured Worker
        // output can see exactly how the redelivered envelope was handled —
        // never relied upon by any assertion above.
        ["Serilog__MinimumLevel__Default"] = "Debug",
    };

    private Dictionary<string, string?> BuildApiEnvironment(string signingKeyPem)
    {
        var values = new Dictionary<string, string?>();
        foreach (var (key, value) in BuildWorkerEnvironment(signingKeyPem))
        {
            if (key == "Serilog__MinimumLevel__Default") continue;
            values[key] = value;
        }
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

    private async Task<long> CountCleaningCancelledAuditEntriesAsync(Guid tenantId, Guid cleaningId)
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

    /// <summary>Test-only diagnostic infrastructure — see this file's own doc comment.</summary>
    private sealed class RabbitMqProbe : IAsyncDisposable
    {
        public required IConnection Connection { get; init; }
        public required IChannel Channel { get; init; }
        public required string Queue { get; init; }

        public async ValueTask DisposeAsync()
        {
            await Channel.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }

    private async Task<RabbitMqProbe> DeclareReservationCancelledProbeQueueAsync()
    {
        var connection = await CreateProbeConnectionAsync();
        var channel = await connection.CreateChannelAsync();

        var queue = $"test-reservation-cancelled-envelope-probe-{Guid.NewGuid():N}";
        await channel.QueueDeclareAsync(queue, durable: false, exclusive: true, autoDelete: true);
        await channel.QueueBindAsync(queue, "reservation-events", "reservation_cancelled");

        return new RabbitMqProbe { Connection = connection, Channel = channel, Queue = queue };
    }

    private async Task<RabbitMqProbe> DeclareCleaningCancelledProbeQueueAsync()
    {
        var connection = await CreateProbeConnectionAsync();
        var channel = await connection.CreateChannelAsync();

        var queue = $"test-cleaning-cancelled-probe-{Guid.NewGuid():N}";
        await channel.QueueDeclareAsync(queue, durable: false, exclusive: true, autoDelete: true);
        await channel.QueueBindAsync(queue, "housekeeping-events", "cleaning_cancelled");

        return new RabbitMqProbe { Connection = connection, Channel = channel, Queue = queue };
    }

    private async Task<IConnection> CreateProbeConnectionAsync()
    {
        var connectionFactory = new ConnectionFactory
        {
            HostName = _rabbitMqContainer.Hostname,
            UserName = RabbitMqBuilder.DefaultUsername,
            Password = RabbitMqBuilder.DefaultPassword,
            VirtualHost = "/",
        };
        return await connectionFactory.CreateConnectionAsync();
    }

    private static async Task<BasicGetResult?> BasicGetWithRetryAsync(IChannel channel, string queue, TimeSpan timeout)
    {
        BasicGetResult? result = null;
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            result = await channel.BasicGetAsync(queue, autoAck: false);
            if (result is not null) break;
            await Task.Delay(TimeSpan.FromMilliseconds(300));
        }
        return result;
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
