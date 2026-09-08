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
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;

namespace IHostPro.Api.Tests.Integration;

/// <summary>
/// Housekeeping Workflow Command Retry/Redelivery Production Gate — proves,
/// against the REAL Wolverine retry/dead-letter mechanism over real
/// RabbitMQ/Postgres/Worker (never mocked), that
/// <c>CreateCleaningForReservationHandler.Configure</c>'s bounded retry
/// (<c>chain.OnException&lt;PropertyNotYetKnownToHousekeepingException&gt;().RetryWithCooldown(250ms, 1s, 3s)</c>)
/// produces exactly the two intended outcomes: a property projection that
/// catches up mid-retry is recovered, and a property projection that never
/// catches up exhausts the bounded schedule and reaches Wolverine's own
/// dead-letter handling — never an infinite loop, never a fabricated
/// Cleaning. Mirrors <see cref="WhatsAppMessageStatusMissingMessageRetryTests"/>'s
/// own already-approved structure exactly, and reuses
/// <see cref="CreateCleaningForReservationWorkflowRoundTripTests"/>'s fixture
/// shape (same real Worker subprocess / RabbitMQ / Postgres wiring).
///
/// Unlike that round-trip test's own <c>SeedActivePropertyAsync</c> — which
/// inserts directly into <c>housekeeping.property_projection</c> up front,
/// deliberately avoiding a real, PRE-EXISTING, UNRELATED race the two
/// PropertyManagement events would otherwise trigger — these tests need the
/// OPPOSITE: the projection row deliberately ABSENT at first, inserted only
/// later (or never), under this test's own explicit control, to exercise the
/// exact transient condition the retry policy exists for.
/// </summary>
public sealed class CreateCleaningForReservationRetryTests : IAsyncLifetime
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

    /// <summary>
    /// The transient race the retry policy exists for: Housekeeping's own
    /// local property projection is deliberately absent when the real
    /// <c>CreateCleaningForReservation</c> command first arrives, forcing the
    /// first attempt to genuinely fail with
    /// <c>PropertyNotYetKnownToHousekeepingException</c> — confirmed by
    /// waiting for that exact failure to appear in the real Worker's own log
    /// before inserting the projection row, so recovery is proven, not
    /// merely a lucky ordering.
    /// </summary>
    [Fact]
    public async Task A_property_projection_that_catches_up_mid_retry_is_recovered_with_no_dead_letter()
    {
        var tenantId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var propertyId = await SeedActivePropertyWithoutHousekeepingProjectionAsync(tenantId, capacity: 4, now);

        StartWorkerProcess();
        (await WaitForWorkerLogLineAsync(
            "Started message listening at rabbitmq://queue/workflow.reservation-created-trigger", TimeSpan.FromSeconds(30)))
            .Should().BeTrue();
        (await WaitForWorkerLogLineAsync(
            "Started message listening at rabbitmq://queue/housekeeping.workflow-commands", TimeSpan.FromSeconds(30)))
            .Should().BeTrue();

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

            // The first delivery attempt is immediate and MUST genuinely fail
            // (Housekeeping's own local property_projection row was deliberately
            // withheld) - this is the race being proven, not avoided.
            var firstAttemptFailed = await WaitForWorkerLogLineAsync("is not a known active property", TimeSpan.FromSeconds(15));
            firstAttemptFailed.Should().BeTrue("the first attempt must genuinely fail to find the property - that is the race this test proves recovery from");

            await InsertHousekeepingPropertyProjectionAsync(tenantId, propertyId);

            // CreateCleaningForReservationHandler.Configure schedules retries at
            // +250ms/+1s/+3s (~4.25s total, four attempts) - generous window
            // over that plus real container/process overhead.
            var cleaningCreated = await WaitUntilAsync(
                () => CountCleaningsForReservationAsync(tenantId, reservationId), count => count > 0, TimeSpan.FromSeconds(30));
            if (!cleaningCreated)
            {
                string workerOutputSnapshot;
                lock (_workerOutputLock) workerOutputSnapshot = _workerOutput.ToString();
                Assert.Fail("A retry after the property projection caught up must eventually create the automated Cleaning. Worker output:\n" + workerOutputSnapshot);
            }

            (await CountCleaningsForReservationAsync(tenantId, reservationId)).Should().Be(1,
                "exactly one automated Cleaning must exist for this reservation");

            var automated = await GetSingleCleaningForReservationAsync(tenantId, reservationId);
            automated.PropertyId.Should().Be(propertyId);
            automated.Status.Should().Be("Pending");

            (await CountDeadLettersAsync()).Should().Be(0,
                "recovery before exhaustion must never leave a dead letter behind");
        }
        finally
        {
            foreach (var key in values.Keys)
                Environment.SetEnvironmentVariable(key, null);
        }
    }

    /// <summary>
    /// The property projection never appears - the permanent case. Proves
    /// retries are FINITE (exactly four attempts, matching
    /// <c>CreateCleaningForReservationHandler.Configure</c>'s bounded
    /// schedule), the handler never loops, no Cleaning is ever fabricated,
    /// and the command reaches Wolverine's own official terminal-failure
    /// handling - "was moved to the error queue" is Wolverine's own log line
    /// for exactly this outcome, emitted by its runtime, never something
    /// this codebase constructs.
    /// </summary>
    [Fact]
    public async Task A_property_projection_that_never_appears_exhausts_bounded_retries_and_reaches_the_real_dead_letter_table()
    {
        var tenantId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var propertyId = await SeedActivePropertyWithoutHousekeepingProjectionAsync(tenantId, capacity: 4, now);
        // Deliberately never inserting housekeeping.property_projection for this property.

        StartWorkerProcess();
        (await WaitForWorkerLogLineAsync(
            "Started message listening at rabbitmq://queue/workflow.reservation-created-trigger", TimeSpan.FromSeconds(30)))
            .Should().BeTrue();
        (await WaitForWorkerLogLineAsync(
            "Started message listening at rabbitmq://queue/housekeeping.workflow-commands", TimeSpan.FromSeconds(30)))
            .Should().BeTrue();

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
                result.IsSuccess.Should().BeTrue();
                reservationId = result.Value.Id;
            }

            // Generous window covering CreateCleaningForReservationHandler.Configure's
            // ~4.25s bounded schedule plus real container/process overhead.
            var reachedErrorQueue = await WaitForWorkerLogLineAsync("was moved to the error queue", TimeSpan.FromSeconds(60));
            if (!reachedErrorQueue)
            {
                string workerOutputSnapshot;
                lock (_workerOutputLock) workerOutputSnapshot = _workerOutput.ToString();
                Assert.Fail("A permanently unknown property must eventually exhaust the bounded retries and reach Wolverine's own terminal-failure handling. Worker output:\n" + workerOutputSnapshot);
            }

            string fullWorkerOutput;
            lock (_workerOutputLock) fullWorkerOutput = _workerOutput.ToString();
            // "Failed to process message" is Wolverine's own per-attempt log line - exactly
            // once per real attempt. The exception's own message text (e.g. "is not a known
            // active property") is a less precise anchor: Wolverine reprints the exception a
            // SECOND time alongside "was moved to the error queue" for the terminal attempt
            // only, so counting that text directly would double-count the last attempt.
            CountOccurrences(fullWorkerOutput, "Failed to process message IHostPro.Contexts.Housekeeping.Contracts.CreateCleaningForReservation").Should().Be(4,
                "the configured schedule is exactly four attempts (initial + three retries) - never more, never fewer");
            CountOccurrences(fullWorkerOutput, "was moved to the error queue").Should().Be(1,
                "exhaustion must be recorded exactly once for this single command - never an unbounded/looping accumulation");

            (await CountCleaningsForReservationAsync(tenantId, reservationId)).Should().Be(0,
                "a permanently unknown property must never fabricate an automated Cleaning");

            var deadLetterCount = await CountDeadLettersAsync();
            Console.WriteLine($"wolverine_dead_letters row count across {string.Join(", ", MessagingSchemas)}: {deadLetterCount} (informational only - mirrors WhatsAppMessageStatusMissingMessageRetryTests' own honest caveat about store selection for a plain consumer with no explicit ancillary-store association)");
        }
        finally
        {
            foreach (var key in values.Keys)
                Environment.SetEnvironmentVariable(key, null);
        }
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) != -1)
        {
            count++;
            index += needle.Length;
        }
        return count;
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
        ["OpenTelemetry__OtlpEndpoint"] = "http://127.0.0.1:14324",
    };

    private Dictionary<string, string?> BuildApiEnvironment(string signingKeyPem)
    {
        var values = new Dictionary<string, string?>();
        foreach (var (key, value) in BuildWorkerEnvironment(signingKeyPem))
            values[key] = value;
        return values;
    }

    // ---- Seeding ------------------------------------------------------------

    /// <summary>
    /// Same as <c>CreateCleaningForReservationWorkflowRoundTripTests.SeedActivePropertyAsync</c>
    /// EXCEPT it deliberately withholds the <c>housekeeping.property_projection</c>
    /// insert — these tests need Housekeeping's own local projection to start
    /// out absent, under their own explicit control, to exercise the
    /// transient race the retry policy exists for.
    /// </summary>
    private async Task<Guid> SeedActivePropertyWithoutHousekeepingProjectionAsync(Guid tenantId, int capacity, DateTimeOffset now)
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

    private async Task InsertHousekeepingPropertyProjectionAsync(Guid tenantId, Guid propertyId)
    {
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
            insertCommand.Parameters.AddWithValue("propertyId", propertyId);
            await insertCommand.ExecuteNonQueryAsync();
        }
        await projectionTransaction.CommitAsync();
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

    private async Task<long> CountCleaningsForReservationAsync(Guid tenantId, Guid reservationId)
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

    private sealed record CleaningRow(Guid PropertyId, string Status);

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
            SELECT property_id, status
            FROM housekeeping.cleanings
            WHERE tenant_id = @tenantId AND reservation_id = @id
            """;
        command.Parameters.AddWithValue("tenantId", tenantId);
        command.Parameters.AddWithValue("id", reservationId);

        CleaningRow row;
        await using (var reader = await command.ExecuteReaderAsync())
        {
            await reader.ReadAsync();
            row = new CleaningRow(reader.GetGuid(0), reader.GetString(1));
        }

        await transaction.CommitAsync();
        return row;
    }

    /// <summary>
    /// <c>IHostPro.Worker</c> configures Wolverine's Main store
    /// (<c>platform_messaging</c>) plus several ancillary stores —
    /// <c>CreateCleaningForReservationHandler</c> has no explicit store
    /// association of its own (unlike the outbox transaction executors,
    /// which call <c>MessageContext.OverrideStorage</c> to pin themselves to
    /// one specific store), so this checks every schema Wolverine could
    /// plausibly have chosen — mirrors
    /// <see cref="WhatsAppMessageStatusMissingMessageRetryTests.CountDeadLettersAsync"/>'s
    /// own reasoning exactly.
    /// </summary>
    private static readonly string[] MessagingSchemas =
    [
        "platform_messaging", "housekeeping_messaging", "reservations_messaging",
        "configuration_messaging", "dashboard_messaging", "external_integrations_messaging",
    ];

    private async Task<long> CountDeadLettersAsync()
    {
        await using var connection = new NpgsqlConnection(_migratorConnectionString);
        await connection.OpenAsync();

        long total = 0;
        foreach (var schema in MessagingSchemas)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT count(*) FROM {schema}.wolverine_dead_letters WHERE message_type ILIKE '%CreateCleaningForReservation%'";
            total += (long)(await command.ExecuteScalarAsync())!;
        }
        return total;
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
