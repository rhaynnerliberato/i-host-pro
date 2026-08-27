using System.Diagnostics;
using System.Security.Cryptography;
using FluentAssertions;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
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
using RabbitMQ.Client;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;

namespace IHostPro.Api.Tests.Integration;

/// <summary>
/// First real transport gate mandated before generalizing Dashboard's
/// consumers (Fase 7, Incremento 2 — Dashboard &amp; Reporting Foundation,
/// Checkpoint 1, §43): <c>ReservationCreated</c> published through
/// Reservations' own real durable outbox, delivered over a real RabbitMQ
/// broker, consumed by a real, unmodified <c>IHostPro.Worker.dll</c>
/// subprocess through <c>IDashboardMessageExecutionScope</c> — proving
/// <c>CheckInAt</c>/<c>CheckOutAt</c>/<c>TenantId</c>/RLS/same-tenant/
/// cross-tenant/idempotency, mirroring <c>ReservationCreatedWorkerRoundTripTests</c>
/// (the Agenda's own equivalent gate) and reusing
/// <c>ReservationCancelledRedeliveryTests</c>' exact wire-envelope-capture
/// technique for the idempotency proof.
/// </summary>
public sealed class DashboardReservationProjectionWorkerRoundTripTests : IAsyncLifetime
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
    public async Task ReservationCreated_delivered_through_real_RabbitMQ_to_a_real_Worker_process_populates_the_Dashboard_projection_with_correct_tenant_isolation_and_idempotency()
    {
        var tenantId = Guid.NewGuid();
        var otherTenantId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var checkInAt = now.AddDays(10);
        var checkOutAt = now.AddDays(14);

        var propertyId = await SeedActivePropertyAsync(tenantId, capacity: 4, now);

        StartWorkerProcess();
        var workerReady = await WaitForWorkerLogLineAsync(
            "Started message listening at rabbitmq://queue/dashboard.reservation-projection", TimeSpan.FromSeconds(30));
        workerReady.Should().BeTrue("the real Worker must report listening to dashboard.reservation-projection before the event is published");

        // Probe bound to the exact same routing key Dashboard's own queue is
        // bound to — captures an identical copy of whatever gets published,
        // used below to prove real redelivery is a harmless no-op.
        await using var envelopeProbe = await DeclareReservationCreatedProbeQueueAsync();

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
                    checkInAt, checkOutAt, GuestCount: 2));
                result.IsSuccess.Should().BeTrue("the seeded Property must be genuinely eligible for a new reservation");
                reservationId = result.Value.Id;
            }

            // ---- Poll the real Dashboard schema for the real Worker's real
            // projection write — never asserted instantly, delivery is
            // genuinely asynchronous over the real broker ----
            var projection = await WaitUntilNotNullAsync(
                () => ReadReservationProjectionAsync(tenantId, reservationId), TimeSpan.FromSeconds(30));
            if (projection is null)
            {
                string workerOutputSnapshot;
                lock (_workerOutputLock) workerOutputSnapshot = _workerOutput.ToString();
                Assert.Fail("The real Worker must consume the real ReservationCreated event and populate dashboard.reservation_projection within 30s. Worker output:\n" + workerOutputSnapshot);
            }

            projection!.Value.PropertyId.Should().Be(propertyId);
            projection.Value.Status.Should().Be("confirmed");
            projection.Value.CheckInAt.Should().BeCloseTo(checkInAt, TimeSpan.FromSeconds(1));
            projection.Value.CheckOutAt.Should().BeCloseTo(checkOutAt, TimeSpan.FromSeconds(1));

            // ---- Cross-tenant isolation: the SAME reservationId, queried
            // under a DIFFERENT tenant's RLS context, must never resolve. ----
            (await ReadReservationProjectionAsync(otherTenantId, reservationId)).Should().BeNull(
                "a different tenant's RLS-scoped connection must never see this tenant's Dashboard projection row");

            // ---- Idempotency: capture the exact wire envelope the real
            // Dashboard queue also received, then redeliver those exact
            // bytes/properties directly onto the real queue — a genuine
            // second delivery, not a synthetic one. ----
            var captured = await BasicGetWithRetryAsync(envelopeProbe.Channel, envelopeProbe.Queue, TimeSpan.FromSeconds(15));
            captured.Should().NotBeNull("the probe, bound to the same routing key as dashboard.reservation-projection, must have received an identical copy of the published ReservationCreated");
            await envelopeProbe.Channel.BasicAckAsync(captured!.DeliveryTag, multiple: false);

            var redeliveredProperties = new BasicProperties(captured.BasicProperties);
            await envelopeProbe.Channel.BasicPublishAsync(
                exchange: string.Empty,
                routingKey: "dashboard.reservation-projection",
                mandatory: false,
                basicProperties: redeliveredProperties,
                body: captured.Body,
                cancellationToken: CancellationToken.None);

            // Give the real Worker a real window to (attempt to) process the
            // redelivered envelope, then assert no duplicate/regressed effect.
            await Task.Delay(TimeSpan.FromSeconds(5));

            var projectionAfterRedelivery = await ReadReservationProjectionAsync(tenantId, reservationId);
            projectionAfterRedelivery.Should().NotBeNull();
            projectionAfterRedelivery!.Value.Status.Should().Be("confirmed", "redelivery of the same ReservationCreated must never regress the row");
            projectionAfterRedelivery.Value.CheckInAt.Should().BeCloseTo(checkInAt, TimeSpan.FromSeconds(1));
            projectionAfterRedelivery.Value.CheckOutAt.Should().BeCloseTo(checkOutAt, TimeSpan.FromSeconds(1));

            (await CountReservationProjectionRowsAsync(tenantId, reservationId)).Should().Be(1,
                "redelivery of the same ReservationCreated must never create a duplicate row (idempotent by construction — checks existence before insert)");
        }
        finally
        {
            foreach (var key in values.Keys)
                Environment.SetEnvironmentVariable(key, null);
        }
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
        return property.Id;
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

    private async Task<(Guid PropertyId, string Status, DateTimeOffset? CheckInAt, DateTimeOffset? CheckOutAt)?> ReadReservationProjectionAsync(
        Guid tenantId, Guid reservationId)
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
        command.CommandText = "SELECT property_id, status, check_in_at, check_out_at FROM dashboard.reservation_projection WHERE tenant_id = @tenantId AND reservation_id = @id";
        command.Parameters.AddWithValue("tenantId", tenantId);
        command.Parameters.AddWithValue("id", reservationId);

        (Guid, string, DateTimeOffset?, DateTimeOffset?)? result = null;
        await using (var reader = await command.ExecuteReaderAsync())
        {
            if (await reader.ReadAsync())
            {
                result = (
                    reader.GetGuid(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetFieldValue<DateTimeOffset>(2),
                    reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3));
            }
        }
        await transaction.CommitAsync();
        return result;
    }

    private async Task<long> CountReservationProjectionRowsAsync(Guid tenantId, Guid reservationId)
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
        command.CommandText = "SELECT count(*) FROM dashboard.reservation_projection WHERE tenant_id = @tenantId AND reservation_id = @id";
        command.Parameters.AddWithValue("tenantId", tenantId);
        command.Parameters.AddWithValue("id", reservationId);
        var count = (long)(await command.ExecuteScalarAsync())!;
        await transaction.CommitAsync();
        return count;
    }

    private static async Task<T?> WaitUntilNotNullAsync<T>(Func<Task<T?>> getValue, TimeSpan timeout) where T : struct
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var value = await getValue();
            if (value is not null) return value;
            await Task.Delay(TimeSpan.FromMilliseconds(300));
        }
        return await getValue();
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

    private async Task<RabbitMqProbe> DeclareReservationCreatedProbeQueueAsync()
    {
        var connection = await CreateProbeConnectionAsync();
        var channel = await connection.CreateChannelAsync();

        var queue = $"test-dashboard-reservation-created-probe-{Guid.NewGuid():N}";
        await channel.QueueDeclareAsync(queue, durable: false, exclusive: true, autoDelete: true);
        await channel.QueueBindAsync(queue, "reservation-events", "reservation_created");

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
