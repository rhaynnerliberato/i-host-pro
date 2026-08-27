using System.Diagnostics;
using System.Security.Cryptography;
using FluentAssertions;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.Contexts.GuestOperations.Application;
using IHostPro.Contexts.PropertyManagement.Domain;
using IHostPro.Contexts.PropertyManagement.Domain.ValueObjects;
using IHostPro.Contexts.PropertyManagement.Infrastructure.Persistence;
using IHostPro.Contexts.Reservations.Application;
using IHostPro.Contexts.Reservations.Application.Reservations;
using IHostPro.Contexts.Reservations.Contracts;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;

namespace IHostPro.Api.Tests.Integration;

/// <summary>
/// Real end-to-end proof of Fase 10, Checkpoint 1 (Guest Operations
/// Foundation) AND Checkpoint 2 (Check-in/Checkout Core) together — the
/// full real chain, no manual seeding of any Guest Operations state:
/// <c>CreateReservationCommand</c> publishes a real <c>ReservationCreated</c>,
/// delivered over a real RabbitMQ broker to a real, unmodified
/// <c>IHostPro.Worker.dll</c> subprocess, consumed by Guest Operations' own
/// choreography (<c>ReservationCreatedGuestStayInitializer</c>), which
/// auto-creates a real <c>GuestStayOperation</c> row (Active) — the resolved
/// creation-trigger governance gate. Check-in and checkout are then real
/// HTTP-command dispatches through <see cref="IGuestOperationsRequestDispatcher"/>
/// (Checkpoint 2's own Mediator-based dispatch, mirroring
/// <see cref="IReservationsRequestDispatcher"/>'s own shape), published
/// through Guest Operations' real durable outbox
/// (<c>guest_operations_messaging</c>), delivered over the SAME broker to
/// Workflow's <c>GuestCheckedOutCloseReservationOrchestrator</c>, which sends
/// the real cross-context command <c>CloseReservation</c> — itself delivered
/// over the SAME real broker, on the existing workflow-orchestration-commands
/// exchange (a second routing key), to Reservations' own
/// <c>CloseReservationCommandHandler</c> — which closes a real
/// <c>Reservation</c> row. Never calls a Guest Operations/Reservations
/// handler directly for the primary chain — mirrors
/// <see cref="CreateCleaningForReservationWorkflowRoundTripTests"/>'s own
/// structure exactly.
/// </summary>
public sealed class GuestCheckedOutCloseReservationWorkerRoundTripTests : IAsyncLifetime
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
    public async Task GuestCheckedOut_flows_through_real_Workflow_and_Reservations_Wolverine_chain_to_close_a_real_Reservation()
    {
        var tenantId = Guid.NewGuid();
        var otherTenantId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        var propertyId = await SeedActivePropertyAsync(tenantId, capacity: 4, now);

        StartWorkerProcess();
        var guestCheckedOutListening = await WaitForWorkerLogLineAsync(
            "Started message listening at rabbitmq://queue/workflow.guest-checked-out-trigger", TimeSpan.FromSeconds(45));
        if (!guestCheckedOutListening)
        {
            string workerOutputSnapshot;
            lock (_workerOutputLock) workerOutputSnapshot = _workerOutput.ToString();
            Assert.Fail("Worker never reported listening to workflow.guest-checked-out-trigger. Worker output:\n" + workerOutputSnapshot);
        }

        var closeReservationListening = await WaitForWorkerLogLineAsync(
            "Started message listening at rabbitmq://queue/reservations.workflow-commands", TimeSpan.FromSeconds(30));
        if (!closeReservationListening)
        {
            string workerOutputSnapshot;
            lock (_workerOutputLock) workerOutputSnapshot = _workerOutput.ToString();
            Assert.Fail("Worker never reported listening to reservations.workflow-commands. Worker output:\n" + workerOutputSnapshot);
        }

        var guestOperationsReservationCreatedListening = await WaitForWorkerLogLineAsync(
            "Started message listening at rabbitmq://queue/guestoperations.reservation-created-trigger", TimeSpan.FromSeconds(30));
        if (!guestOperationsReservationCreatedListening)
        {
            string workerOutputSnapshot;
            lock (_workerOutputLock) workerOutputSnapshot = _workerOutput.ToString();
            Assert.Fail("Worker never reported listening to guestoperations.reservation-created-trigger. Worker output:\n" + workerOutputSnapshot);
        }

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
                    now.AddDays(-3), now.AddDays(-1), GuestCount: 2));
                result.IsSuccess.Should().BeTrue("the seeded Property must be genuinely eligible for a new reservation");
                reservationId = result.Value.Id;
            }

            // ---- No manual seeding: GuestStayOperation must be auto-created by
            // the real choreography consumer (ReservationCreatedGuestStayInitializer,
            // running in the real Worker subprocess) reacting to the real
            // ReservationCreated published above — the resolved creation-trigger
            // governance gate (Checkpoint 2). Poll, never assert instantly:
            // delivery is genuinely asynchronous over a real broker hop. ----
            var created = await WaitUntilAsync(
                () => GetGuestStayOperationStatusAsync(tenantId, reservationId), status => status == "Active", TimeSpan.FromSeconds(30));
            if (!created)
            {
                string workerOutputSnapshot;
                lock (_workerOutputLock) workerOutputSnapshot = _workerOutput.ToString();
                Assert.Fail("The real ReservationCreated -> Guest Operations choreography must auto-create an Active GuestStayOperation within 30s. Worker output:\n" + workerOutputSnapshot);
            }

            // ---- The real trigger: dispatch RecordGuestCheckedInCommand then
            // RecordGuestCheckedOutCommand through IGuestOperationsRequestDispatcher
            // (Checkpoint 2's real HTTP-command dispatch shape) — from here on,
            // everything flows through real transport. Two SEPARATE scopes,
            // never one shared scope: a real HTTP client would call the two
            // endpoints as two distinct requests, each getting its own fresh
            // ASP.NET Core request scope — sharing one scope here would reuse
            // the same Scoped IDbContextOutbox<GuestOperationsDbContext>
            // instance across two sequential flushes, an artificial test
            // shortcut a real client can never produce. ----
            using (var checkInScope = factory.Services.CreateScope())
            {
                checkInScope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(tenantId);
                var dispatcher = checkInScope.ServiceProvider.GetRequiredService<IGuestOperationsRequestDispatcher>();

                var checkInResult = await dispatcher.Send(new RecordGuestCheckedInCommand
                {
                    TenantId = tenantId,
                    ReservationId = reservationId,
                }, CancellationToken.None);
                checkInResult.IsSuccess.Should().BeTrue("the auto-created GuestStayOperation must be Active and therefore eligible for check-in");
            }

            using (var checkOutScope = factory.Services.CreateScope())
            {
                checkOutScope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(tenantId);
                var dispatcher = checkOutScope.ServiceProvider.GetRequiredService<IGuestOperationsRequestDispatcher>();

                var checkOutResult = await dispatcher.Send(new RecordGuestCheckedOutCommand
                {
                    TenantId = tenantId,
                    ReservationId = reservationId,
                }, CancellationToken.None);
                checkOutResult.IsSuccess.Should().BeTrue("the just-CheckedIn GuestStayOperation must be eligible for checkout");
            }

            // ---- Poll for the real Reservation to become Closed — never
            // asserted instantly, delivery is genuinely asynchronous over two
            // real broker hops (Guest Operations -> Workflow, Workflow ->
            // Reservations). ----
            var closed = await WaitUntilAsync(
                () => GetReservationStatusAsync(tenantId, reservationId), status => status == "Closed", TimeSpan.FromSeconds(30));
            if (!closed)
            {
                string workerOutputSnapshot;
                lock (_workerOutputLock) workerOutputSnapshot = _workerOutput.ToString();
                Assert.Fail("The real Guest Operations -> Workflow -> Reservations chain must close the Reservation within 30s. Worker output:\n" + workerOutputSnapshot);
            }

            (await GetReservationStatusAsync(tenantId, reservationId)).Should().Be("Closed");

            // ---- Structured audit log evidence, real Worker process. ----
            string workerOutputForAuditCheck;
            lock (_workerOutputLock) workerOutputForAuditCheck = _workerOutput.ToString();
            workerOutputForAuditCheck.Should().Contain("Workflow02_GuestCheckedOut")
                .And.Contain("CommandDispatched")
                .And.Contain(tenantId.ToString())
                .And.Contain(reservationId.ToString());
            workerOutputForAuditCheck.Should().Contain("Reservation closed for tenant",
                "CloseReservationCommandHandler's own success log line must appear over real transport");

            // ---- Cross-tenant isolation: the SAME reservationId, queried
            // under a DIFFERENT tenant's RLS context, must never resolve. ----
            (await GetReservationStatusUnderTenantAsync(otherTenantId, reservationId)).Should().BeNull(
                "a different tenant's RLS-scoped connection must never see this tenant's Reservation");

            // ---- Duplicate delivery / idempotency (Application-level guard,
            // user-approved semantics): invoking ICloseReservationHandler a
            // second time for the SAME reservation, in-process, must never
            // throw and must leave the Reservation Closed — never a duplicate
            // ReservationClosed side effect. ----
            using (var idempotencyScope = factory.Services.CreateScope())
            {
                idempotencyScope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(tenantId);
                var handler = idempotencyScope.ServiceProvider.GetRequiredService<ICloseReservationHandler>();

                var act = async () => await handler.HandleAsync(new CloseReservation
                {
                    TenantId = tenantId,
                    ReservationId = reservationId,
                    CorrelationId = Guid.NewGuid(),
                }, CancellationToken.None);

                await act.Should().NotThrowAsync("a redelivered CloseReservation for an already-Closed Reservation must be a silent idempotent no-op");
            }

            (await GetReservationStatusAsync(tenantId, reservationId)).Should().Be("Closed",
                "a redelivered CloseReservation must never change an already-Closed Reservation's status");

            (await GetGuestStayOperationStatusAsync(tenantId, reservationId)).Should().Be("CheckedOut");
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
        ["ConnectionStrings__Communication"] = _appConnectionString,
        ["ConnectionStrings__ExternalIntegrations"] = _appConnectionString,
        ["ConnectionStrings__GuestOperations"] = _appConnectionString,
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

        return property.Id;
    }

    // ---- DB access --------------------------------------------------------

    private static async Task SetTenantAsync(Microsoft.EntityFrameworkCore.Infrastructure.DatabaseFacade database, Guid tenantId) =>
        await database.ExecuteSqlInterpolatedAsync($"SELECT set_config('app.tenant_id', {tenantId.ToString()}, true)");

    private PropertyManagementDbContext CreatePropertyManagementDbContext(ITenantContext tenantContext)
    {
        var options = new DbContextOptionsBuilder<PropertyManagementDbContext>()
            .UseNpgsql(_migratorConnectionString, npgsqlOptions => npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "property_management"))
            .Options;
        return new PropertyManagementDbContext(options, tenantContext);
    }

    private async Task<string?> GetReservationStatusAsync(Guid tenantId, Guid reservationId) =>
        await GetReservationStatusUnderTenantAsync(tenantId, reservationId);

    private async Task<string?> GetReservationStatusUnderTenantAsync(Guid tenantId, Guid reservationId)
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
        command.CommandText = "SELECT status FROM reservations.reservations WHERE tenant_id = @tenantId AND id = @id";
        command.Parameters.AddWithValue("tenantId", tenantId);
        command.Parameters.AddWithValue("id", reservationId);

        var result = await command.ExecuteScalarAsync();
        await transaction.CommitAsync();
        return result as string;
    }

    private async Task<string?> GetGuestStayOperationStatusAsync(Guid tenantId, Guid reservationId)
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
        command.CommandText = "SELECT status FROM guest_operations.guest_stay_operations WHERE tenant_id = @tenantId AND reservation_id = @reservationId";
        command.Parameters.AddWithValue("tenantId", tenantId);
        command.Parameters.AddWithValue("reservationId", reservationId);

        var result = await command.ExecuteScalarAsync();
        await transaction.CommitAsync();
        return result as string;
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
        psi.Environment["ConnectionStrings__ExternalIntegrations"] = _migratorConnectionString;
        psi.Environment["ConnectionStrings__GuestOperations"] = _migratorConnectionString;
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
