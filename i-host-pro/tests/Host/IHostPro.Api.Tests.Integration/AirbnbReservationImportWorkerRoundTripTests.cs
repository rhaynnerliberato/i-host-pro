using System.Diagnostics;
using System.Security.Cryptography;
using FluentAssertions;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.Contexts.Communication.Domain;
using IHostPro.Contexts.Communication.Infrastructure.Persistence;
using IHostPro.Contexts.ExternalIntegrations.Application.AirbnbImports;
using IHostPro.Contexts.ExternalIntegrations.Domain;
using IHostPro.Contexts.ExternalIntegrations.Infrastructure.Persistence;
using IHostPro.Contexts.PropertyManagement.Application;
using IHostPro.Contexts.PropertyManagement.Application.Properties;
using IHostPro.Contexts.Reservations.Domain;
using IHostPro.Contexts.Reservations.Domain.Enums;
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
/// Fase 9, Checkpoint 3.2 — "Airbnb Deterministic Foundation", mandatory
/// real transport E2E gate (mandate §33): an Airbnb reservation import
/// published through the real <see cref="IAirbnbReservationSyncPublisher"/>
/// (the same call a future real sync process will make) → External
/// Integrations' own real durable outbox → real RabbitMQ → a real,
/// unmodified <c>IHostPro.Worker.dll</c> subprocess → Reservations' own
/// keyed Wolverine consumer → a real, persisted <see cref="Reservation"/>
/// (<c>Source=Airbnb</c>) → the SAME <c>ReservationCreated</c> fan-out a
/// manual reservation produces (Housekeeping/Dashboard/Workflow all still
/// react identically) → Communication's own consent guard skips it (no
/// <see cref="Message"/> ever created). Mirrors
/// <c>ReservationCreatedCommunicationWorkerRoundTripTests</c>'s structure
/// exactly — no real Airbnb network call anywhere in this test
/// (<c>AirbnbPartnerAccessAvailable=false</c> remains an external blocker).
/// </summary>
public sealed class AirbnbReservationImportWorkerRoundTripTests : IAsyncLifetime
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

        // WolverineConfigurationExtensions.UseIHostProRabbitMq (shared by
        // every Host process) has no RabbitMq:Port configuration key at all
        // — it always connects on RabbitMQ's default port 5672. A dynamic
        // Testcontainers port therefore cannot work here; the fixed binding
        // mirrors ReservationCreatedCommunicationWorkerRoundTripTests'
        // own established precedent exactly (same accepted collision risk
        // with a locally-running dev RabbitMQ container on port 5672).
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
    public async Task AirbnbReservationImported_delivered_through_real_RabbitMQ_creates_a_real_Reservation_fans_out_and_Communication_skips()
    {
        var tenantId = Guid.NewGuid();
        var externalListingId = $"listing-{Guid.NewGuid():N}";
        var externalReservationId = $"airbnb-res-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;

        StartWorkerProcess();
        var reservationsReady = await WaitForWorkerLogLineAsync(
            "Started message listening at rabbitmq://queue/reservations.airbnb-import", TimeSpan.FromSeconds(30));
        reservationsReady.Should().BeTrue("the real Worker must report listening to Reservations' own Airbnb import queue before the event is published");
        var propertyProjectionReady = await WaitForWorkerLogLineAsync(
            "Started message listening at rabbitmq://queue/housekeeping.property-projection", TimeSpan.FromSeconds(5));
        propertyProjectionReady.Should().BeTrue();
        var housekeepingReady = await WaitForWorkerLogLineAsync(
            "Started message listening at rabbitmq://queue/housekeeping.reservation-projection", TimeSpan.FromSeconds(5));
        housekeepingReady.Should().BeTrue();
        var dashboardReady = await WaitForWorkerLogLineAsync(
            "Started message listening at rabbitmq://queue/dashboard.reservation-projection", TimeSpan.FromSeconds(5));
        dashboardReady.Should().BeTrue();
        var workflowReady = await WaitForWorkerLogLineAsync(
            "Started message listening at rabbitmq://queue/workflow.reservation-created-trigger", TimeSpan.FromSeconds(5));
        workflowReady.Should().BeTrue("Workflow's own trigger consumer must be listening before the event is published");
        var housekeepingWorkflowCommandsReady = await WaitForWorkerLogLineAsync(
            "Started message listening at rabbitmq://queue/housekeeping.workflow-commands", TimeSpan.FromSeconds(5));
        housekeepingWorkflowCommandsReady.Should().BeTrue("Housekeeping's own CreateCleaningForReservation command consumer must be listening before the event is published");

        // A real, ACTIVE Property, created and activated through the real
        // dispatcher (never a direct DbContext insert) — Housekeeping's own
        // CreateCleaningForReservationCommandHandler rejects any PropertyId
        // absent from its local property_projection, which is populated only
        // by really consuming PropertyCreated/PropertyActivated through the
        // real RabbitMQ transport, exactly like it would for a manual
        // reservation's real Property.
        var propertyId = await SeedActivePropertyThroughRealDispatchAsync(tenantId, capacity: 4);
        await SeedAirbnbListingMappingAsync(tenantId, externalListingId, propertyId, now);

        await PublishImportAsync(tenantId, externalListingId, externalReservationId, now);

        var reservation = await WaitForReservationAsync(tenantId, externalReservationId, TimeSpan.FromSeconds(30));
        reservation.Should().NotBeNull("the real Worker must consume the real AirbnbReservationImported event and materialize a real Reservation within 30s");
        reservation!.Source.Should().Be(ReservationSource.Airbnb);
        reservation.ExternalReservationId.Should().Be(externalReservationId);
        reservation.PropertyId.Should().Be(propertyId, "the PropertyId must come from the resolved AirbnbListingMapping, never a raw external listing id");
        reservation.GuestPhone.Should().BeNull("CP3.1 Decision Gate item 12 (Option A): the import event never carries a guest phone");

        var housekeepingProjected = await WaitUntilAsync(
            () => HousekeepingProjectionExistsAsync(tenantId, reservation.Id), exists => exists, TimeSpan.FromSeconds(15));
        housekeepingProjected.Should().BeTrue("Housekeeping must react to an Airbnb-imported ReservationCreated exactly like a manual one");

        var dashboardProjected = await WaitUntilAsync(
            () => DashboardProjectionExistsAsync(tenantId, reservation.Id), exists => exists, TimeSpan.FromSeconds(15));
        dashboardProjected.Should().BeTrue("Dashboard must react to an Airbnb-imported ReservationCreated exactly like a manual one");

        // Longer window than the direct-projection checks above: this hop
        // is a real three-step chain (ReservationCreated -> Workflow's own
        // consumer -> a NEW durable-outbox CreateCleaningForReservation
        // command -> Housekeeping's own second consumer), each step subject
        // to Wolverine's durable-outbox polling latency, not just one direct
        // consumer reacting to the original event.
        var cleaningCreatedByWorkflow = await WaitUntilAsync(
            () => HousekeepingCleaningExistsAsync(tenantId, reservation.Id), exists => exists, TimeSpan.FromSeconds(30));
        if (!cleaningCreatedByWorkflow)
        {
            string workerOutputSnapshot;
            lock (_workerOutputLock) workerOutputSnapshot = _workerOutput.ToString();
            Assert.Fail("Workflow's own orchestration (ReservationCreated -> CreateCleaningForReservation) must also fire for an Airbnb-imported reservation. Worker output:\n" + workerOutputSnapshot);
        }

        // ---- Communication consent guard: no Message may ever be created ----
        await Task.Delay(TimeSpan.FromSeconds(3));
        var messageCount = await CountCommunicationMessagesAsync(tenantId, reservation.Id);
        messageCount.Should().Be(0, "Communication must skip an Airbnb-imported reservation — no established WhatsApp consent exists (CP3.1 Decision Gate item 20)");
    }

    [Fact]
    public async Task AirbnbReservationImported_published_twice_creates_only_one_Reservation()
    {
        var tenantId = Guid.NewGuid();
        var externalListingId = $"listing-{Guid.NewGuid():N}";
        var externalReservationId = $"airbnb-res-{Guid.NewGuid():N}";
        var propertyId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        await SeedAirbnbListingMappingAsync(tenantId, externalListingId, propertyId, now);

        StartWorkerProcess();
        var reservationsReady = await WaitForWorkerLogLineAsync(
            "Started message listening at rabbitmq://queue/reservations.airbnb-import", TimeSpan.FromSeconds(30));
        reservationsReady.Should().BeTrue();

        await PublishImportAsync(tenantId, externalListingId, externalReservationId, now);

        var reservation = await WaitForReservationAsync(tenantId, externalReservationId, TimeSpan.FromSeconds(30));
        reservation.Should().NotBeNull("the first, unique delivery must be fully processed before the duplicate is published");

        await PublishImportAsync(tenantId, externalListingId, externalReservationId, now.AddSeconds(1));

        await Task.Delay(TimeSpan.FromSeconds(5));

        var count = await CountReservationsAsync(tenantId, externalReservationId);
        count.Should().Be(1, "publishing the same AirbnbReservationImported twice must never create a second Reservation");
    }

    // ---- Publish side (real ExternalIntegrations outbox) -------------------

    private async Task PublishImportAsync(Guid tenantId, string externalListingId, string externalReservationId, DateTimeOffset now)
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
            var publisher = scope.ServiceProvider.GetRequiredService<IAirbnbReservationSyncPublisher>();

            var result = await publisher.PublishReservationImportedAsync(
                externalListingId, externalReservationId, "Test Guest",
                now.AddDays(1), now.AddDays(5), guestCount: 2,
                occurredAtUtc: now, correlationId: Guid.NewGuid(), CancellationToken.None);

            result.IsSuccess.Should().BeTrue("the seeded AirbnbListingMapping must resolve successfully");
        }
        finally
        {
            foreach (var key in values.Keys)
                Environment.SetEnvironmentVariable(key, null);
        }
    }

    // ---- Seeding ------------------------------------------------------------

    private async Task SeedAirbnbListingMappingAsync(Guid tenantId, string externalListingId, Guid propertyId, DateTimeOffset now)
    {
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);
        await using var dbContext = CreateExternalIntegrationsDbContext(tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext.Database, tenantId);

        var integration = AirbnbIntegration.Create(Guid.NewGuid(), tenantId, now);
        dbContext.AirbnbIntegrations.Add(integration);

        var mapping = AirbnbListingMapping.Create(Guid.NewGuid(), tenantId, integration.Id, externalListingId, propertyId, now);
        dbContext.AirbnbListingMappings.Add(mapping);

        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();
    }

    /// <summary>
    /// Creates then activates a real Property through the real
    /// <see cref="IPropertyManagementRequestDispatcher"/> (never a direct
    /// DbContext insert) — mirrors <c>PropertyEventsWorkerRoundTripTests</c>'
    /// own established pattern exactly: a fresh DI scope per command
    /// dispatch, and a poll for Housekeeping's own real
    /// <c>property_projection</c> row (populated only by really consuming
    /// PropertyCreated/PropertyActivated through RabbitMQ) to reach
    /// <c>IsActive=true</c> before returning, so the caller never races
    /// ahead of Housekeeping's own eventually-consistent projection.
    /// </summary>
    private async Task<Guid> SeedActivePropertyThroughRealDispatchAsync(Guid tenantId, int capacity)
    {
        using var signingKey = RSA.Create(2048);
        var values = BuildApiEnvironment(signingKey.ExportRSAPrivateKeyPem());
        foreach (var (key, value) in values)
            Environment.SetEnvironmentVariable(key, value);

        try
        {
            using var factory = new WebApplicationFactory<Program>();

            Guid propertyId;
            using (var scope = factory.Services.CreateScope())
            {
                scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(tenantId);
                var dispatcher = scope.ServiceProvider.GetRequiredService<IPropertyManagementRequestDispatcher>();

                var address = new PropertyAddressInput("59090-000", "Rua Exemplo", "100", null, "Ponta Negra", "Natal", "RN", "BR");
                var createResult = await dispatcher.Send(new CreatePropertyCommand(
                    tenantId, Guid.NewGuid(), $"ABNB-{Guid.NewGuid():N}"[..12], "Test Property", capacity,
                    CondominiumId: null, address));
                createResult.IsSuccess.Should().BeTrue("the seeded Property must be created successfully");
                propertyId = createResult.Value.Id;
            }

            using (var scope = factory.Services.CreateScope())
            {
                scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(tenantId);
                var dispatcher = scope.ServiceProvider.GetRequiredService<IPropertyManagementRequestDispatcher>();

                var activateResult = await dispatcher.Send(new ActivatePropertyCommand(tenantId, Guid.NewGuid(), propertyId));
                activateResult.IsSuccess.Should().BeTrue("the seeded Property must activate successfully");
            }

            var active = await WaitUntilAsync(
                () => HousekeepingPropertyProjectionIsActiveAsync(tenantId, propertyId), isActive => isActive, TimeSpan.FromSeconds(15));
            active.Should().BeTrue("Housekeeping's own property_projection must reflect the real PropertyActivated event before an Airbnb import can be published");

            return propertyId;
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
        ["OpenTelemetry__OtlpEndpoint"] = "http://127.0.0.1:14322",
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

    private ExternalIntegrationsDbContext CreateExternalIntegrationsDbContext(ITenantContext tenantContext)
    {
        var options = new DbContextOptionsBuilder<ExternalIntegrationsDbContext>()
            .UseNpgsql(_migratorConnectionString, npgsqlOptions => npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "external_integrations"))
            .Options;
        return new ExternalIntegrationsDbContext(options, tenantContext);
    }

    private ReservationsDbContext CreateReservationsDbContext(ITenantContext tenantContext)
    {
        var options = new DbContextOptionsBuilder<ReservationsDbContext>()
            .UseNpgsql(_migratorConnectionString, npgsqlOptions => npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "reservations"))
            .Options;
        return new ReservationsDbContext(options, tenantContext);
    }

    private CommunicationDbContext CreateCommunicationDbContext(ITenantContext tenantContext)
    {
        var options = new DbContextOptionsBuilder<CommunicationDbContext>()
            .UseNpgsql(_migratorConnectionString, npgsqlOptions => npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "communication"))
            .Options;
        return new CommunicationDbContext(options, tenantContext);
    }

    private async Task<Reservation?> WaitForReservationAsync(Guid tenantId, string externalReservationId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var reservation = await ReadReservationAsync(tenantId, externalReservationId);
            if (reservation is not null)
                return reservation;
            await Task.Delay(TimeSpan.FromMilliseconds(300));
        }
        return await ReadReservationAsync(tenantId, externalReservationId);
    }

    private async Task<Reservation?> ReadReservationAsync(Guid tenantId, string externalReservationId)
    {
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);
        await using var dbContext = CreateReservationsDbContext(tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext.Database, tenantId);

        var reservation = await dbContext.Reservations.AsNoTracking()
            .FirstOrDefaultAsync(r => r.TenantId == tenantId && r.ExternalReservationId == externalReservationId);

        await transaction.CommitAsync();
        return reservation;
    }

    private async Task<int> CountReservationsAsync(Guid tenantId, string externalReservationId)
    {
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);
        await using var dbContext = CreateReservationsDbContext(tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext.Database, tenantId);

        var count = await dbContext.Reservations.CountAsync(r => r.TenantId == tenantId && r.ExternalReservationId == externalReservationId);

        await transaction.CommitAsync();
        return count;
    }

    private async Task<int> CountCommunicationMessagesAsync(Guid tenantId, Guid reservationId)
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

    private async Task<bool> HousekeepingCleaningExistsAsync(Guid tenantId, Guid reservationId)
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
        return count > 0;
    }

    private async Task<bool> HousekeepingPropertyProjectionIsActiveAsync(Guid tenantId, Guid propertyId)
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
        command.CommandText = "SELECT count(*) FROM housekeeping.property_projection WHERE tenant_id = @tenantId AND property_id = @propertyId AND is_active";
        command.Parameters.AddWithValue("tenantId", tenantId);
        command.Parameters.AddWithValue("propertyId", propertyId);
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

    private static string FindSolutionRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "IHostPro.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException($"Could not locate IHostPro.sln walking up from {AppContext.BaseDirectory}.");
    }
}
