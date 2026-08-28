using System.Diagnostics;
using System.Security.Cryptography;
using FluentAssertions;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.Contexts.Communication.Domain;
using IHostPro.Contexts.Communication.Infrastructure.Persistence;
using IHostPro.Contexts.Configuration.Application;
using IHostPro.Contexts.Configuration.Application.Templates;
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
/// Fase 9, Checkpoint 1 — "Comunicação e Integrações do MVP", mandatory real
/// transport E2E gate: a Reservation created through the real
/// <see cref="IReservationsRequestDispatcher"/> (the same call
/// <c>ReservationsController.CreateReservation</c> makes) → Reservations'
/// own real durable outbox → real RabbitMQ → a real, unmodified
/// <c>IHostPro.Worker.dll</c> subprocess → Communication's own keyed
/// Wolverine consumer → the real active Template (Configuration) → the real
/// <see cref="IHostPro.Contexts.Reservations.Contracts.IReservationGuestContactReader"/>
/// (ADR-019) → a persisted <see cref="Message"/> → the fake WhatsApp
/// connector (CP1's only implementation) → a terminal <c>Sent</c> status.
/// Also proves the real fan-out (Housekeeping/Dashboard still receive the
/// SAME <c>ReservationCreated</c>) and PII-absence (guest phone never
/// appears in the Worker's own log output). Mirrors
/// <see cref="ReservationCreatedWorkerRoundTripTests"/>'s structure exactly.
/// </summary>
public sealed class ReservationCreatedCommunicationWorkerRoundTripTests : IAsyncLifetime
{
    private const string AppRolePassword = "test_app_password";
    private const string MigratorRolePassword = "test_migrator_password";
    private const string GuestPhone = "+5511998887766";

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
    public async Task ReservationCreated_delivered_through_real_RabbitMQ_reaches_Communication_and_all_other_real_consumers_without_leaking_PII()
    {
        var tenantId = Guid.NewGuid();
        var otherTenantId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        var propertyId = await SeedActivePropertyAsync(tenantId, capacity: 4, now);
        await SeedActiveTemplateAsync(tenantId);

        StartWorkerProcess();
        var communicationReady = await WaitForWorkerLogLineAsync(
            "Started message listening at rabbitmq://queue/communication.reservation-created-trigger", TimeSpan.FromSeconds(30));
        communicationReady.Should().BeTrue("the real Worker must report listening to Communication's own queue before the event is published");
        var housekeepingReady = await WaitForWorkerLogLineAsync(
            "Started message listening at rabbitmq://queue/housekeeping.reservation-projection", TimeSpan.FromSeconds(5));
        housekeepingReady.Should().BeTrue("Housekeeping's existing consumer must still be listening — Communication must never displace it");
        var dashboardReady = await WaitForWorkerLogLineAsync(
            "Started message listening at rabbitmq://queue/dashboard.reservation-projection", TimeSpan.FromSeconds(5));
        dashboardReady.Should().BeTrue("Dashboard's existing consumer must still be listening — Communication must never displace it");

        using var signingKey = RSA.Create(2048);
        var values = BuildApiEnvironment(signingKey.ExportRSAPrivateKeyPem());
        foreach (var (key, value) in values)
            Environment.SetEnvironmentVariable(key, value);

        Guid reservationId;
        try
        {
            using var factory = new WebApplicationFactory<Program>();
            using var scope = factory.Services.CreateScope();
            scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(tenantId);
            var dispatcher = scope.ServiceProvider.GetRequiredService<IReservationsRequestDispatcher>();

            var result = await dispatcher.Send(new CreateReservationCommand(
                tenantId, Guid.NewGuid(), propertyId, "Test Guest", GuestPhone,
                now.AddDays(1), now.AddDays(5), GuestCount: 2));
            result.IsSuccess.Should().BeTrue("the seeded Property must be genuinely eligible for a new reservation");
            reservationId = result.Value.Id;
        }
        finally
        {
            foreach (var key in values.Keys)
                Environment.SetEnvironmentVariable(key, null);
        }

        // ---- Communication: the real chain must terminate in Sent ----
        var message = await WaitForMessageAsync(tenantId, reservationId, TimeSpan.FromSeconds(30));
        if (message is null)
        {
            string workerOutputSnapshot;
            lock (_workerOutputLock) workerOutputSnapshot = _workerOutput.ToString();
            Assert.Fail("The real Worker must consume the real ReservationCreated event, resolve the real Template/guest contact, dispatch through the fake connector, and persist a Message within 30s. Worker output:\n" + workerOutputSnapshot);
        }
        message!.Status.Should().Be(MessageStatus.Sent);
        message.Channel.Should().Be("WhatsApp");
        message.TemplateKey.Should().Be("RESERVATION_CONFIRMATION");
        message.DestinationMasked.Should().Be("**********7766", "only the last four digits may ever be persisted");

        // ---- Cross-tenant isolation of the Message row ----
        (await ReadMessageAsync(otherTenantId, reservationId)).Should().BeNull(
            "a different tenant's RLS-scoped connection must never see this tenant's Message");

        // ---- Fan-out: the SAME ReservationCreated must still reach
        // Housekeeping's and Dashboard's own existing projections ----
        var housekeepingProjected = await WaitUntilAsync(
            () => HousekeepingProjectionExistsAsync(tenantId, reservationId), exists => exists, TimeSpan.FromSeconds(15));
        housekeepingProjected.Should().BeTrue("Communication's new consumer must never break Housekeeping's own existing fan-out");

        var dashboardProjected = await WaitUntilAsync(
            () => DashboardProjectionExistsAsync(tenantId, reservationId), exists => exists, TimeSpan.FromSeconds(15));
        dashboardProjected.Should().BeTrue("Communication's new consumer must never break Dashboard's own existing fan-out");

        // ---- PII-absence: the guest phone must never appear anywhere in
        // the real Worker's own log output (structured audit logging is
        // PII-safe by design, ADR-019 item 11 + CP1 mandate) ----
        string fullWorkerOutput;
        lock (_workerOutputLock) fullWorkerOutput = _workerOutput.ToString();
        fullWorkerOutput.Should().NotContain(GuestPhone, "the guest phone must never be logged by the real Worker process");
    }

    [Fact]
    public async Task ReservationCreated_real_redelivery_to_Communications_own_queue_creates_only_one_Message()
    {
        var tenantId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        var propertyId = await SeedActivePropertyAsync(tenantId, capacity: 4, now);
        await SeedActiveTemplateAsync(tenantId);

        StartWorkerProcess();
        var communicationReady = await WaitForWorkerLogLineAsync(
            "Started message listening at rabbitmq://queue/communication.reservation-created-trigger", TimeSpan.FromSeconds(30));
        communicationReady.Should().BeTrue();

        // Second, independent binding to the same real routing key — an
        // identical copy of whatever gets published, mirrors
        // ReservationCancelledRedeliveryTests' own established technique.
        await using var envelopeProbe = await DeclareReservationCreatedProbeQueueAsync();

        using var signingKey = RSA.Create(2048);
        var values = BuildApiEnvironment(signingKey.ExportRSAPrivateKeyPem());
        foreach (var (key, value) in values)
            Environment.SetEnvironmentVariable(key, value);

        Guid reservationId;
        try
        {
            using var factory = new WebApplicationFactory<Program>();
            using var scope = factory.Services.CreateScope();
            scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(tenantId);
            var dispatcher = scope.ServiceProvider.GetRequiredService<IReservationsRequestDispatcher>();

            var result = await dispatcher.Send(new CreateReservationCommand(
                tenantId, Guid.NewGuid(), propertyId, "Test Guest", GuestPhone,
                now.AddDays(1), now.AddDays(5), GuestCount: 2));
            result.IsSuccess.Should().BeTrue();
            reservationId = result.Value.Id;
        }
        finally
        {
            foreach (var key in values.Keys)
                Environment.SetEnvironmentVariable(key, null);
        }

        var message = await WaitForMessageAsync(tenantId, reservationId, TimeSpan.FromSeconds(30));
        message.Should().NotBeNull("the first, unique delivery must be fully processed before redelivery is attempted");
        message!.Status.Should().Be(MessageStatus.Sent);

        var captured = await BasicGetWithRetryAsync(envelopeProbe.Channel, envelopeProbe.Queue, TimeSpan.FromSeconds(15));
        captured.Should().NotBeNull("the probe, bound to the same routing key as Communication's own queue, must have received an identical copy");
        await envelopeProbe.Channel.BasicAckAsync(captured!.DeliveryTag, multiple: false);

        // ---- Real redelivery: the exact same bytes/AMQP properties,
        // published a second time directly onto Communication's own queue
        // via the default exchange. ----
        var redeliveredProperties = new BasicProperties(captured.BasicProperties);
        await envelopeProbe.Channel.BasicPublishAsync(
            exchange: string.Empty,
            routingKey: "communication.reservation-created-trigger",
            mandatory: false,
            basicProperties: redeliveredProperties,
            body: captured.Body,
            cancellationToken: CancellationToken.None);

        await Task.Delay(TimeSpan.FromSeconds(5));

        var count = await CountMessagesAsync(tenantId, reservationId);
        count.Should().Be(1, "redelivery of the same envelope must never create a second Message (CP1 idempotency)");
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

    /// <summary>
    /// Dispatches the real <see cref="CreateTemplateCommand"/> through the
    /// real <see cref="IConfigurationRequestDispatcher"/> (the same call
    /// <c>TemplatesController.Create</c> makes) — never a direct DbContext
    /// insert — so this test exercises the real Template ownership/creation
    /// path Communication depends on (CP1 mandate: Communication resolves
    /// this exact key, <c>RESERVATION_CONFIRMATION</c>, with the one
    /// documented variable, <c>CheckInDate</c>).
    /// </summary>
    private async Task SeedActiveTemplateAsync(Guid tenantId)
    {
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

            var result = await dispatcher.Send(new CreateTemplateCommand(tenantId, "RESERVATION_CONFIRMATION", "Check-in em {{CheckInDate}}"));
            result.IsSuccess.Should().BeTrue("the real Template must be created successfully before the reservation that triggers Communication");
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
        ["DOTNET_ENVIRONMENT"] = "Development",
        ["ConnectionStrings__Identity"] = _appConnectionString,
        ["ConnectionStrings__PropertyManagement"] = _appConnectionString,
        ["ConnectionStrings__Reservations"] = _appConnectionString,
        ["ConnectionStrings__Configuration"] = _appConnectionString,
        ["ConnectionStrings__Housekeeping"] = _appConnectionString,
        ["ConnectionStrings__Dashboard"] = _appConnectionString,
        ["ConnectionStrings__Communication"] = _appConnectionString,
        ["ConnectionStrings__GuestOperations"] = _appConnectionString,
        ["ConnectionStrings__Payments"] = _appConnectionString,
        ["ConnectionStrings__ExternalIntegrations"] = _appConnectionString,
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

    private async Task<Message?> WaitForMessageAsync(Guid tenantId, Guid reservationId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var message = await ReadMessageAsync(tenantId, reservationId);
            if (message is not null && message.Status is MessageStatus.Sent or MessageStatus.Failed)
                return message;
            await Task.Delay(TimeSpan.FromMilliseconds(300));
        }
        return await ReadMessageAsync(tenantId, reservationId);
    }

    private async Task<Message?> ReadMessageAsync(Guid tenantId, Guid reservationId)
    {
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);
        await using var dbContext = CreateCommunicationDbContext(tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext.Database, tenantId);

        var message = await dbContext.Messages.AsNoTracking()
            .FirstOrDefaultAsync(m => m.TenantId == tenantId && m.ReservationId == reservationId);

        await transaction.CommitAsync();
        return message;
    }

    private async Task<int> CountMessagesAsync(Guid tenantId, Guid reservationId)
    {
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);
        await using var dbContext = CreateCommunicationDbContext(tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext.Database, tenantId);

        var count = await dbContext.Messages.CountAsync(m => m.TenantId == tenantId && m.ReservationId == reservationId);

        await transaction.CommitAsync();
        return count;
    }

    private CommunicationDbContext CreateCommunicationDbContext(ITenantContext tenantContext)
    {
        var options = new DbContextOptionsBuilder<CommunicationDbContext>()
            .UseNpgsql(_migratorConnectionString, npgsqlOptions => npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "communication"))
            .Options;
        return new CommunicationDbContext(options, tenantContext);
    }

    private async Task<bool> HousekeepingProjectionExistsAsync(Guid tenantId, Guid reservationId)
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
        command.CommandText = "SELECT count(*) FROM housekeeping.reservation_projection WHERE tenant_id = @tenantId AND reservation_id = @id";
        command.Parameters.AddWithValue("tenantId", tenantId);
        command.Parameters.AddWithValue("id", reservationId);
        var count = (long)(await command.ExecuteScalarAsync())!;
        await transaction.CommitAsync();
        return count > 0;
    }

    private async Task<bool> DashboardProjectionExistsAsync(Guid tenantId, Guid reservationId)
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
        return count > 0;
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
        psi.Environment["ConnectionStrings__Dashboard"] = _migratorConnectionString;
        psi.Environment["ConnectionStrings__Communication"] = _migratorConnectionString;
        psi.Environment["ConnectionStrings__GuestOperations"] = _migratorConnectionString;
        psi.Environment["ConnectionStrings__Payments"] = _migratorConnectionString;
        psi.Environment["ConnectionStrings__ExternalIntegrations"] = _migratorConnectionString;
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

    /// <summary>Test-only diagnostic infrastructure — mirrors <c>ReservationCancelledRedeliveryTests</c>'s own probe technique.</summary>
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

        var queue = $"test-reservation-created-communication-probe-{Guid.NewGuid():N}";
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

    private static string FindSolutionRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "IHostPro.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException($"Could not locate IHostPro.sln walking up from {AppContext.BaseDirectory}.");
    }
}
