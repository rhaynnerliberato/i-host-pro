using System.Diagnostics;
using System.Security.Cryptography;
using FluentAssertions;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.Contexts.Housekeeping.Application;
using IHostPro.Contexts.Housekeeping.Application.Cleanings;
using IHostPro.Contexts.Identity.Domain;
using IHostPro.Contexts.Identity.Domain.ValueObjects;
using IHostPro.Contexts.Identity.Infrastructure.Persistence;
using IHostPro.Contexts.PropertyManagement.Application;
using IHostPro.Contexts.PropertyManagement.Application.Properties;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;

namespace IHostPro.Api.Tests.Integration;

/// <summary>
/// Fase 7, Incremento 1 (Agenda Foundation), Checkpoint 1 CLOSURE, item 12:
/// the second real transport gate required by the user's closure
/// authorization — a genuine POST-creation status update, not just the
/// original creation gate already proven by
/// <see cref="CleaningCreatedScheduleProjectionWorkerRoundTripTests"/>. Also
/// proves the real production routing defect fix (Documento 07 §29.4):
/// <c>CleaningNeedsHelp</c> has had a real producer since Fase 6 Incremento
/// 2A, but <c>IHostPro.Api</c> never routed it to RabbitMQ at all until this
/// closure round — this test is the first real, end-to-end proof that the
/// fix actually delivers the event.
///
/// Full real chain: Create Cleaning (Pending) → Assign (real
/// <c>IIdentityUserEligibilityReader</c> lookup against a real, directly
/// seeded HOUSEKEEPER user — mirrors <c>HousekeepingEndpointsTests</c>'s own
/// <c>SeedHousekeeperUserAsync</c> pattern) → Start (own-cleaning, Assigned
/// → Started) → report NeedsHelp (own-cleaning, Started → WaitingHelp,
/// publishes the newly-routed <c>CleaningNeedsHelp</c>) → real Housekeeping
/// outbox → real RabbitMQ → real, unmodified <c>IHostPro.Worker.dll</c>
/// subprocess → the SAME Reservations schedule projection row created by
/// the first gate, now updated in place.
/// </summary>
public sealed class CleaningNeedsHelpScheduleProjectionWorkerRoundTripTests : IAsyncLifetime
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
        Directory.CreateDirectory(Path.GetDirectoryName(DiagnosticLogPath)!);
        File.WriteAllText(DiagnosticLogPath, string.Empty);

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
    public async Task CleaningNeedsHelp_delivered_through_real_RabbitMQ_to_a_real_Worker_process_updates_the_existing_schedule_projection_row_to_WaitingHelp()
    {
        var tenantId = Guid.NewGuid();
        var otherTenantId = Guid.NewGuid();
        var scheduledAtUtc = new DateTimeOffset(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);

        StartWorkerProcess();
        var propertyQueueReady = await WaitForWorkerLogLineAsync(
            "Started message listening at rabbitmq://queue/housekeeping.property-projection", TimeSpan.FromSeconds(30));
        propertyQueueReady.Should().BeTrue("the real Worker must report listening to housekeeping.property-projection before any event is published");
        var scheduleQueueReady = await WaitForWorkerLogLineAsync(
            "Started message listening at rabbitmq://queue/reservations.cleaning-schedule-projection", TimeSpan.FromSeconds(30));
        scheduleQueueReady.Should().BeTrue("the real Worker must report listening to reservations.cleaning-schedule-projection before CleaningNeedsHelp is published");

        using var signingKey = RSA.Create(2048);
        var values = BuildApiEnvironment(signingKey.ExportRSAPrivateKeyPem());
        foreach (var (key, value) in values)
            Environment.SetEnvironmentVariable(key, value);

        try
        {
            using var factory = new WebApplicationFactory<Program>();

            // ---- Seed a real, Active Property (same pattern as
            // CleaningCreatedScheduleProjectionWorkerRoundTripTests) so the
            // real Worker naturally populates Housekeeping's own local
            // property_projection — CreateCleaningCommandHandler's gate. ----
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

            // ---- Seed a real, directly-persisted HOUSEKEEPER user in
            // Identity's own real database — mirrors HousekeepingEndpointsTests'
            // SeedHousekeeperUserAsync exactly, since AssignCleaningCommandHandler
            // requires a real IIdentityUserEligibilityReader lookup to
            // succeed (exists, Active, same tenant, holds HOUSEKEEPER). ----
            var housekeeperUserId = await SeedHousekeeperUserAsync(tenantId);

            await using var cleaningAssignedProbe = await DeclareProbeQueueAsync("cleaning_assigned");

            var administratorActorId = Guid.NewGuid();
            Guid cleaningId;
            using (var scope = factory.Services.CreateScope())
            {
                scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(tenantId);
                var housekeepingDispatcher = scope.ServiceProvider.GetRequiredService<IHousekeepingRequestDispatcher>();

                var createCleaningResult = await housekeepingDispatcher.Send(
                    new CreateCleaningCommand(tenantId, administratorActorId, propertyId, ReservationId: null, scheduledAtUtc));
                createCleaningResult.IsSuccess.Should().BeTrue("Cleaning creation must succeed against a known-active Property");
                cleaningId = createCleaningResult.Value.Id;
            }

            // ---- Wait for the FIRST projection write (CleaningCreated) —
            // already proven in isolation by
            // CleaningCreatedScheduleProjectionWorkerRoundTripTests; this
            // gate re-confirms it only as the precondition for the row this
            // test's real subject (CleaningNeedsHelp) must update in place,
            // never insert a second row for. ----
            var initialProjection = await WaitUntilProjectionAsync(tenantId, cleaningId, TimeSpan.FromSeconds(30));
            initialProjection.Should().NotBeNull("CleaningCreated must reach the projection before the subsequent transitions are published");
            initialProjection!.Value.Status.Should().Be("Pending");

            // ---- Assign (Pending -> Assigned) — a fresh DI scope per
            // command dispatch, same DI-scope-reuse-drops-events defect this
            // checkpoint's own CleaningCreated gate already documented and
            // fixed. ----
            using (var scope = factory.Services.CreateScope())
            {
                scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(tenantId);
                var housekeepingDispatcher = scope.ServiceProvider.GetRequiredService<IHousekeepingRequestDispatcher>();

                var assignResult = await housekeepingDispatcher.Send(
                    new AssignCleaningCommand(tenantId, administratorActorId, cleaningId, housekeeperUserId));
                assignResult.IsSuccess.Should().BeTrue("assignment to a real, eligible HOUSEKEEPER must succeed");
            }

            RabbitMQ.Client.BasicGetResult? assignedDelivered = null;
            var probeDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
            while (DateTime.UtcNow < probeDeadline)
            {
                assignedDelivered = await cleaningAssignedProbe.Channel.BasicGetAsync(cleaningAssignedProbe.Queue, autoAck: true);
                if (assignedDelivered is not null) break;
                await Task.Delay(TimeSpan.FromMilliseconds(300));
            }
            assignedDelivered.Should().NotBeNull("CleaningAssigned must be published onto housekeeping-events/cleaning_assigned — stage 4-6 evidence");
            assignedDelivered!.RoutingKey.Should().Be("cleaning_assigned");

            var afterAssign = await WaitUntilStatusAsync(tenantId, cleaningId, "Assigned", TimeSpan.FromSeconds(90));
            if (afterAssign is null)
            {
                var lastObserved = await ProjectionEntryAsync(tenantId, cleaningId);
                string workerOutputSnapshot;
                lock (_workerOutputLock) workerOutputSnapshot = _workerOutput.ToString();
                Assert.Fail(
                    $"CleaningAssigned was confirmed published (probe) but never reached the projection within 90s. " +
                    $"Last observed status: {lastObserved?.Status ?? "(row missing)"}.\nWorker output:\n{workerOutputSnapshot}");
            }

            // ---- Start (own-cleaning, Assigned -> Started). ----
            using (var scope = factory.Services.CreateScope())
            {
                scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(tenantId);
                var housekeepingDispatcher = scope.ServiceProvider.GetRequiredService<IHousekeepingRequestDispatcher>();

                var startResult = await housekeepingDispatcher.Send(
                    new StartOwnCleaningCommand(tenantId, housekeeperUserId, cleaningId));
                startResult.IsSuccess.Should().BeTrue("the assigned housekeeper must be able to start their own cleaning");
            }

            var afterStart = await WaitUntilStatusAsync(tenantId, cleaningId, "Started", TimeSpan.FromSeconds(15));
            if (afterStart is null)
            {
                var lastObserved = await ProjectionEntryAsync(tenantId, cleaningId);
                Assert.Fail($"DIAGNOSTIC: CleaningStarted never reached the projection within 15s. Last observed status: {lastObserved?.Status ?? "(row missing)"}.");
            }

            // ---- Report NeedsHelp (own-cleaning, Started -> WaitingHelp) —
            // THIS is the real subject: publishes CleaningNeedsHelp, which
            // IHostPro.Api never routed to RabbitMQ before this checkpoint's
            // closure fix. ----
            using (var scope = factory.Services.CreateScope())
            {
                scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(tenantId);
                var housekeepingDispatcher = scope.ServiceProvider.GetRequiredService<IHousekeepingRequestDispatcher>();

                var waitingHelpResult = await housekeepingDispatcher.Send(
                    new MarkOwnCleaningWaitingHelpCommand(tenantId, housekeeperUserId, cleaningId));
                waitingHelpResult.IsSuccess.Should().BeTrue("the assigned housekeeper must be able to report needing help on their own, Started cleaning");
            }

            // ---- Poll the real Reservations database for the real
            // Worker's real projection update. ----
            var finalProjection = await WaitUntilStatusAsync(tenantId, cleaningId, "WaitingHelp", TimeSpan.FromSeconds(30));
            if (finalProjection is null)
            {
                string workerOutputSnapshot;
                lock (_workerOutputLock) workerOutputSnapshot = _workerOutput.ToString();
                var lastObserved = await ProjectionEntryAsync(tenantId, cleaningId);
                var outboxDump = await DumpHousekeepingOutboxAsync();
                Assert.Fail(
                    "The real Worker must consume the real CleaningNeedsHelp event and update the schedule projection to WaitingHelp within 30s. " +
                    $"Last observed projection status: {lastObserved?.Status ?? "(row missing)"}. " +
                    $"housekeeping_messaging.wolverine_outgoing_envelopes:\n{outboxDump}\n" +
                    "Worker output:\n" + workerOutputSnapshot);
            }

            finalProjection!.Value.PropertyId.Should().Be(propertyId, "the row updated must be the SAME one CleaningCreated originally created");
            finalProjection.Value.ScheduledAtUtc.Should().Be(scheduledAtUtc, "a status-only transition must never alter ScheduledAtUtc");
            finalProjection.Value.AssignedHousekeeperUserId.Should().Be(housekeeperUserId, "the housekeeper assigned earlier must survive the WaitingHelp transition, never wiped");
            finalProjection.Value.Status.Should().Be("WaitingHelp");

            (await CountProjectionRowsAsync(tenantId, cleaningId)).Should().Be(
                1, "the whole Create->Assign->Start->NeedsHelp chain must update a single row, never insert a second one for the same Cleaning");

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

    // ---- Identity seeding (mirrors HousekeepingEndpointsTests.SeedHousekeeperUserAsync) ----

    private async Task EnsureTenantExistsAsync(Guid tenantId)
    {
        await using var dbContext = CreateIdentityDbContext(_migratorConnectionString, new TenantContext());

        if (await dbContext.Tenants.AnyAsync(t => t.Id == tenantId))
            return;

        var tenant = Tenant.Provision(tenantId, TenantSlug.Create($"test-{tenantId:N}"), "Test Tenant", DateTimeOffset.UtcNow);
        dbContext.Tenants.Add(tenant);
        await dbContext.SaveChangesAsync();
    }

    private async Task<Guid> SeedHousekeeperUserAsync(Guid tenantId)
    {
        await EnsureTenantExistsAsync(tenantId);

        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);

        await using var dbContext = CreateIdentityDbContext(_migratorConnectionString, tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetIdentityTenantAsync(dbContext, tenantId);

        var email = Email.Create($"housekeeper-{Guid.NewGuid():N}@example.com");
        var user = User.Register(Guid.NewGuid(), tenantId, email, "Test Housekeeper", PasswordHash.FromEncoded("$argon2id$v=19$test"), DateTimeOffset.UtcNow);
        dbContext.Users.Add(user);
        dbContext.UserRoles.Add(new UserRole(tenantId, user.Id, "HOUSEKEEPER", DateTimeOffset.UtcNow, assignedByUserId: null));

        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();

        return user.Id;
    }

    private static async Task SetIdentityTenantAsync(IdentityDbContext dbContext, Guid tenantId) =>
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.tenant_id', {tenantId.ToString()}, true)");

    private static IdentityDbContext CreateIdentityDbContext(string connectionString, ITenantContext tenantContext)
    {
        var options = new DbContextOptionsBuilder<IdentityDbContext>()
            .UseNpgsql(connectionString, npgsqlOptions => npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "identity"))
            .Options;
        return new IdentityDbContext(options, tenantContext);
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

    private sealed class RabbitProbe : IAsyncDisposable
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

    private async Task<RabbitProbe> DeclareProbeQueueAsync(string routingKey)
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

        var queue = $"test-{routingKey}-probe-{Guid.NewGuid():N}";
        await channel.QueueDeclareAsync(queue, durable: false, exclusive: true, autoDelete: true);
        await channel.QueueBindAsync(queue, "housekeeping-events", routingKey);

        return new RabbitProbe { Connection = connection, Channel = channel, Queue = queue };
    }

    private async Task<string> DumpHousekeepingOutboxAsync()
    {
        await using var connection = new NpgsqlConnection(_migratorConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT message_type, destination, attempts FROM housekeeping_messaging.wolverine_outgoing_envelopes";
        await using var reader = await command.ExecuteReaderAsync();
        var lines = new List<string>();
        while (await reader.ReadAsync())
            lines.Add($"message_type={reader.GetString(0)} destination={reader.GetString(1)} attempts={reader.GetInt32(2)}");
        return lines.Count == 0 ? "(empty)" : string.Join("\n", lines);
    }

    private async Task<int> CountProjectionRowsAsync(Guid tenantId, Guid cleaningId)
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
            SELECT COUNT(*) FROM reservations.cleaning_schedule_projection
            WHERE tenant_id = @tenantId AND cleaning_id = @cleaningId
            """;
        command.Parameters.AddWithValue("tenantId", tenantId);
        command.Parameters.AddWithValue("cleaningId", cleaningId);

        var count = (long)(await command.ExecuteScalarAsync())!;
        await transaction.CommitAsync();
        return (int)count;
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

    private async Task<(Guid PropertyId, Guid? AssignedHousekeeperUserId, DateTimeOffset? ScheduledAtUtc, string Status)?> WaitUntilStatusAsync(
        Guid tenantId, Guid cleaningId, string expectedStatus, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var entry = await ProjectionEntryAsync(tenantId, cleaningId);
            if (entry is not null && entry.Value.Status == expectedStatus)
                return entry;
            await Task.Delay(TimeSpan.FromMilliseconds(300));
        }
        var final = await ProjectionEntryAsync(tenantId, cleaningId);
        return final is not null && final.Value.Status == expectedStatus ? final : null;
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

    private static readonly string DiagnosticLogPath = Path.Combine(
        Path.GetTempPath(), "claude", "worker-diagnostic.log");

    private void OnWorkerLine(string? line)
    {
        if (line is null) return;
        lock (_workerOutputLock)
        {
            File.AppendAllText(DiagnosticLogPath, line + Environment.NewLine);
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
        ["ConnectionStrings__AIAgent"] = _appConnectionString,
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
        ["OpenTelemetry__OtlpEndpoint"] = "http://127.0.0.1:14325",
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
        psi.Environment["ConnectionStrings__AIAgent"] = _migratorConnectionString;
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
