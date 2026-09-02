using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using FluentAssertions;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.Contexts.Configuration.Application.Policies;
using IHostPro.Contexts.Configuration.Application;
using IHostPro.Contexts.GuestOperations.Application;
using IHostPro.Contexts.Identity.Application;
using IHostPro.Contexts.Identity.Infrastructure.Authentication;
using IHostPro.Contexts.PropertyManagement.Domain;
using IHostPro.Contexts.PropertyManagement.Domain.ValueObjects;
using IHostPro.Contexts.PropertyManagement.Infrastructure.Persistence;
using IHostPro.Contexts.Reservations.Application;
using IHostPro.Contexts.Reservations.Application.Reservations;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;

namespace IHostPro.Api.Tests.Integration;

/// <summary>
/// Real end-to-end proof of Fase 10, Checkpoint 3 (Early Check-in / Late
/// Checkout) — the mandatory gate: no unit/integration test in isolation is
/// accepted as sufficient proof of the real chain (mandate §19). Every
/// scenario here runs against a real Postgres instance, a real RabbitMQ
/// broker, a real unmodified <c>IHostPro.Worker.dll</c> subprocess, and the
/// real HTTP surface of <c>IHostPro.Api</c> (via <see cref="WebApplicationFactory{TEntryPoint}"/>
/// with a real bearer token) — mirrors
/// <c>GuestCheckedOutCloseReservationWorkerRoundTripTests</c>'s own
/// infrastructure exactly, extended with real HTTP+JWT for the command under
/// test (never an in-process dispatcher call for the actual early-check-in/
/// late-checkout request itself — only setup/seeding uses in-process
/// dispatch, mirroring that same precedent).
///
/// One shared <see cref="Fixture"/> (Postgres + RabbitMQ + MigrationRunner +
/// Worker subprocess + Api <see cref="WebApplicationFactory{TEntryPoint}"/>)
/// across every scenario in this file — spinning up a fresh container/
/// subprocess set per scenario would make this suite impractically slow.
/// Every scenario uses its own fresh tenant/property/reservation GUIDs, and
/// the assembly already disables test parallelization
/// (<c>[assembly: CollectionBehavior(DisableTestParallelization = true)]</c>,
/// <c>AssemblyInfo.cs</c>), so scenarios never interleave against the one
/// shared Worker process.
///
/// <see cref="IReservationScheduleReader"/>/<see cref="ICleaningReadinessReader"/>/
/// <see cref="IEarlyCheckInPolicyReader"/>/<see cref="ILateCheckoutPolicyReader"/>
/// are never faked here — every evaluation runs the REAL Infrastructure
/// implementation registered by the real <c>Program.cs</c> composition root,
/// against the real seeded Postgres data (mandate §8).
/// </summary>
public sealed class EarlyCheckInLateCheckoutWorkflowRoundTripTests : IClassFixture<EarlyCheckInLateCheckoutWorkflowRoundTripTests.Fixture>
{
    private readonly Fixture _fixture;

    public EarlyCheckInLateCheckoutWorkflowRoundTripTests(Fixture fixture) => _fixture = fixture;

    public sealed class Fixture : IAsyncLifetime
    {
        private const string AppRolePassword = "test_app_password";
        private const string MigratorRolePassword = "test_migrator_password";
        private const string Issuer = "https://identity.ihostpro.test";
        private const string Audience = "ihostpro-api-test";

        private PostgreSqlContainer _postgresContainer = null!;
        private RabbitMqContainer _rabbitMqContainer = null!;
        private Process? _workerProcess;
        private WebApplicationFactory<Program>? _apiFactory;
        private readonly Dictionary<string, string?> _envValues = [];

        public string MigratorConnectionString { get; private set; } = null!;
        public string AppConnectionString { get; private set; } = null!;
        public HttpClient ApiClient { get; private set; } = null!;
        public IServiceProvider ApiServices => _apiFactory!.Services;

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
            MigratorConnectionString = builder.ConnectionString;
            builder.Username = "ihostpro_app";
            builder.Password = AppRolePassword;
            AppConnectionString = builder.ConnectionString;

            var (migrationExitCode, migrationOutput) = await RunMigrationRunnerAsync();
            if (migrationExitCode != 0)
                throw new InvalidOperationException($"MigrationRunner failed with exit code {migrationExitCode}. Output:\n{migrationOutput}");

            StartWorkerProcess();

            foreach (var queue in new[]
            {
                "guestoperations.reservation-created-trigger",
                "workflow.early-checkin-approved-trigger",
                "workflow.late-checkout-approved-trigger",
                "housekeeping.late-checkout-approved-trigger",
                "reservations.workflow-commands",
            })
            {
                var listening = await WaitForWorkerLogLineAsync($"Started message listening at rabbitmq://queue/{queue}", TimeSpan.FromSeconds(45));
                if (!listening)
                {
                    string snapshot;
                    lock (_workerOutputLock) snapshot = _workerOutput.ToString();
                    throw new InvalidOperationException($"Worker never reported listening to {queue}. Worker output:\n{snapshot}");
                }
            }

            using var signingKey = RSA.Create(2048);
            var signingKeyPem = signingKey.ExportRSAPrivateKeyPem();
            foreach (var (key, value) in BuildApiEnvironment(signingKeyPem))
            {
                _envValues[key] = value;
                Environment.SetEnvironmentVariable(key, value);
            }

            _apiFactory = new WebApplicationFactory<Program>();
            ApiClient = _apiFactory.CreateClient();
        }

        public async Task DisposeAsync()
        {
            ApiClient?.Dispose();
            _apiFactory?.Dispose();

            foreach (var key in _envValues.Keys)
                Environment.SetEnvironmentVariable(key, null);

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

        // ---- Worker subprocess ----

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

        public async Task<bool> WaitForWorkerLogLineAsync(string pattern, TimeSpan timeout)
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

        public string GetWorkerOutputSnapshot()
        {
            lock (_workerOutputLock) return _workerOutput.ToString();
        }

        private Dictionary<string, string?> BuildWorkerEnvironment(string signingKeyPem) => new()
        {
            ["ASPNETCORE_ENVIRONMENT"] = "Development",
            ["DOTNET_ENVIRONMENT"] = "Development",
            ["ConnectionStrings__Identity"] = AppConnectionString,
            ["ConnectionStrings__PropertyManagement"] = AppConnectionString,
            ["ConnectionStrings__Reservations"] = AppConnectionString,
            ["ConnectionStrings__Configuration"] = AppConnectionString,
            ["ConnectionStrings__Housekeeping"] = AppConnectionString,
            ["ConnectionStrings__Communication"] = AppConnectionString,
            ["ConnectionStrings__ExternalIntegrations"] = AppConnectionString,
            ["ConnectionStrings__GuestOperations"] = AppConnectionString,
            ["ConnectionStrings__Payments"] = AppConnectionString,
            ["ConnectionStrings__AIAgent"] = AppConnectionString,
            ["ConnectionStrings__Dashboard"] = AppConnectionString,
            ["ConnectionStrings__Platform"] = AppConnectionString,
            ["Identity__Jwt__Issuer"] = Issuer,
            ["Identity__Jwt__Audience"] = Audience,
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
            psi.Environment["ConnectionStrings__Identity"] = MigratorConnectionString;
            psi.Environment["ConnectionStrings__PropertyManagement"] = MigratorConnectionString;
            psi.Environment["ConnectionStrings__Reservations"] = MigratorConnectionString;
            psi.Environment["ConnectionStrings__Configuration"] = MigratorConnectionString;
            psi.Environment["ConnectionStrings__Housekeeping"] = MigratorConnectionString;
            psi.Environment["ConnectionStrings__Communication"] = MigratorConnectionString;
            psi.Environment["ConnectionStrings__ExternalIntegrations"] = MigratorConnectionString;
            psi.Environment["ConnectionStrings__GuestOperations"] = MigratorConnectionString;
            psi.Environment["ConnectionStrings__Payments"] = MigratorConnectionString;
            psi.Environment["ConnectionStrings__AIAgent"] = MigratorConnectionString;
            psi.Environment["ConnectionStrings__Dashboard"] = MigratorConnectionString;
            psi.Environment["ConnectionStrings__Platform"] = MigratorConnectionString;
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

    // ==================================================================
    // 1. EARLY CHECK-IN — APPROVED
    // ==================================================================

    [Fact]
    public async Task EarlyCheckIn_Approved_flows_through_the_real_broker_chain_and_reschedules_the_real_Reservation()
    {
        var tenantId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var checkInAt = now.AddDays(5);
        var checkOutAt = now.AddDays(8);

        var propertyId = await SeedActivePropertyAsync(tenantId, now);
        var reservationId = await SeedConfirmedReservationAsync(tenantId, propertyId, checkInAt, checkOutAt);
        await WaitForGuestStayOperationStatusAsync(tenantId, reservationId, "Active");

        await SeedEarlyCheckInPolicyAsync(tenantId,
            """{"allowed":true,"earliestTime":null,"requiresCleaningCompleted":false,"requiresForm":false,"notifyFrontDesk":false}""");

        var requestedCheckInAt = checkInAt.AddHours(-2);
        var token = await GenerateAdminTokenAsync(tenantId);

        var response = await PostJsonAsync(
            $"/api/v1/guest-operations/reservations/{reservationId}/early-check-in",
            token, new { requestedCheckInAt });

        response.StatusCode.Should().Be(HttpStatusCode.OK, await SafeReadBodyAsync(response));
        var body = await response.Content.ReadFromJsonAsync<EarlyCheckInResponseShape>();
        body!.Status.Should().Be("approved");
        body.DenialReasonCode.Should().BeNull();

        var rescheduled = await WaitUntilAsync(
            () => GetReservationScheduleAsync(tenantId, reservationId),
            schedule => schedule.CheckInAt is not null && Math.Abs((schedule.CheckInAt.Value - requestedCheckInAt).TotalSeconds) < 1,
            TimeSpan.FromSeconds(30));
        rescheduled.Should().BeTrue(
            "the real Guest Operations -> Workflow -> Reservations chain must reschedule the real Reservation within 30s. Worker output:\n" +
            _fixture.GetWorkerOutputSnapshot());

        var finalSchedule = await GetReservationScheduleAsync(tenantId, reservationId);
        finalSchedule.CheckOutAt.Should().BeCloseTo(checkOutAt, TimeSpan.FromSeconds(1), "only CheckInAt may change — CheckOutAt must be preserved");

        var workerOutput = _fixture.GetWorkerOutputSnapshot();
        workerOutput.Should().Contain("Workflow03_EarlyCheckinApproved").And.Contain("CommandDispatched").And.Contain(reservationId.ToString());
        workerOutput.Should().Contain("Reservation rescheduled for early check-in",
            "RescheduleReservationForEarlyCheckInCommandHandler's own real success log line must appear over real transport");

        var requestRow = await GetLatestEarlyCheckInRequestAsync(tenantId, reservationId);
        requestRow.Status.Should().Be("Approved");
    }

    // ==================================================================
    // 2. EARLY CHECK-IN — DENIED (CleaningNotReady)
    // ==================================================================

    [Fact]
    public async Task EarlyCheckIn_Denied_for_CleaningNotReady_never_dispatches_a_reschedule()
    {
        var tenantId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var checkInAt = now.AddDays(6);
        var checkOutAt = now.AddDays(9);

        var propertyId = await SeedActivePropertyAsync(tenantId, now);
        var reservationId = await SeedConfirmedReservationAsync(tenantId, propertyId, checkInAt, checkOutAt);
        await WaitForGuestStayOperationStatusAsync(tenantId, reservationId, "Active");

        // Real choreography (Workflow's ReservationCreatedCleaningOrchestrator
        // -> Housekeeping's CreateCleaningForReservationCommandHandler) must
        // have auto-created a real, still-Pending Cleaning for this
        // Reservation by now — never manually seeded.
        var cleaningReady = await WaitUntilAsync(
            () => GetCleaningAsync(tenantId, reservationId), cleaning => cleaning is not null, TimeSpan.FromSeconds(30));
        cleaningReady.Should().BeTrue("the real ReservationCreated -> Workflow -> Housekeeping choreography must auto-create a Cleaning within 30s");
        (await GetCleaningAsync(tenantId, reservationId))!.Value.Status.Should().Be("Pending");

        await SeedEarlyCheckInPolicyAsync(tenantId,
            """{"allowed":true,"earliestTime":null,"requiresCleaningCompleted":true,"requiresForm":false,"notifyFrontDesk":false}""");

        var requestedCheckInAt = checkInAt.AddHours(-2);
        var token = await GenerateAdminTokenAsync(tenantId);

        var response = await PostJsonAsync(
            $"/api/v1/guest-operations/reservations/{reservationId}/early-check-in",
            token, new { requestedCheckInAt });

        response.StatusCode.Should().Be(HttpStatusCode.OK, await SafeReadBodyAsync(response));
        var body = await response.Content.ReadFromJsonAsync<EarlyCheckInResponseShape>();
        body!.Status.Should().Be("denied");
        body.DenialReasonCode.Should().Be("cleaning_not_ready");

        var requestRow = await GetLatestEarlyCheckInRequestAsync(tenantId, reservationId);
        requestRow.Status.Should().Be("Denied");
        requestRow.DenialReason.Should().Be("CleaningNotReady");

        var finalSchedule = await GetReservationScheduleAsync(tenantId, reservationId);
        finalSchedule.CheckInAt.Should().BeCloseTo(checkInAt, TimeSpan.FromSeconds(1),
            "a Denied request must never trigger any reschedule of the real Reservation");

        _fixture.GetWorkerOutputSnapshot().Should().NotContain($"Reservation rescheduled for early check-in, tenant {tenantId} reservationId {reservationId}");
    }

    // ==================================================================
    // 3. LATE CHECKOUT — APPROVED, NO PIX
    //    Also proves item 6 (Housekeeping's real reaction, gated on
    //    UpdatesCleaning) and item 11 (ADR-020 fan-out isolation): this
    //    SAME LateCheckoutApproved event has TWO independent in-process
    //    consumers (Workflow's reschedule orchestrator, Housekeeping's
    //    audit reactor) — both must fire from the one real publish,
    //    neither stealing or duplicating the other's delivery.
    // ==================================================================

    [Fact]
    public async Task LateCheckout_Approved_without_Pix_reschedules_the_Reservation_and_Housekeeping_observes_it_without_mutating_the_schedule()
    {
        var tenantId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var checkInAt = now.AddDays(5);
        var checkOutAt = now.AddDays(8);

        var propertyId = await SeedActivePropertyAsync(tenantId, now);
        var reservationId = await SeedConfirmedReservationAsync(tenantId, propertyId, checkInAt, checkOutAt);
        await WaitForGuestStayOperationStatusAsync(tenantId, reservationId, "Active");
        await CheckInGuestAsync(tenantId, reservationId);
        await WaitForGuestStayOperationStatusAsync(tenantId, reservationId, "CheckedIn");

        var cleaningExists = await WaitUntilAsync(
            () => GetCleaningAsync(tenantId, reservationId), cleaning => cleaning is not null, TimeSpan.FromSeconds(30));
        cleaningExists.Should().BeTrue("the real ReservationCreated -> Workflow -> Housekeeping choreography must auto-create a Cleaning within 30s");
        var cleaningId = (await GetCleaningAsync(tenantId, reservationId))!.Value.Id;

        await SeedLateCheckoutPolicyAsync(tenantId,
            """{"allowed":true,"latestTime":null,"chargeType":"none","chargeValue":null,"requiresPix":false,"blocksCalendar":false,"updatesCleaning":true}""");

        var requestedCheckOutAt = checkOutAt.AddHours(3);
        var token = await GenerateAdminTokenAsync(tenantId);

        var response = await PostJsonAsync(
            $"/api/v1/guest-operations/reservations/{reservationId}/late-checkout",
            token, new { requestedCheckOutAt });

        response.StatusCode.Should().Be(HttpStatusCode.OK, await SafeReadBodyAsync(response));
        var body = await response.Content.ReadFromJsonAsync<LateCheckoutResponseShape>();
        body!.Status.Should().Be("approved");
        body.RequiresPix.Should().BeFalse();

        var rescheduled = await WaitUntilAsync(
            () => GetReservationScheduleAsync(tenantId, reservationId),
            schedule => Math.Abs((schedule.CheckOutAt - requestedCheckOutAt).TotalSeconds) < 1,
            TimeSpan.FromSeconds(30));
        rescheduled.Should().BeTrue(
            "the real Guest Operations -> Workflow -> Reservations chain must reschedule the real Reservation within 30s. Worker output:\n" +
            _fixture.GetWorkerOutputSnapshot());

        var finalSchedule = await GetReservationScheduleAsync(tenantId, reservationId);
        finalSchedule.CheckInAt.Should().BeCloseTo(checkInAt, TimeSpan.FromSeconds(1), "only CheckOutAt may change — CheckInAt must be preserved");

        var workerOutput = _fixture.GetWorkerOutputSnapshot();
        workerOutput.Should().Contain("Workflow04_LateCheckoutApproved").And.Contain("CommandDispatched").And.Contain(reservationId.ToString());
        workerOutput.Should().Contain("Reservation rescheduled for late checkout",
            "RescheduleReservationForLateCheckoutCommandHandler's own real success log line must appear over real transport");

        // ---- Housekeeping's own, SEPARATE reaction to the SAME event
        // (ADR-020 second consumer) — an audit entry only, never a
        // ScheduledAtUtc mutation (no documented rule exists for computing
        // one this checkpoint). ----
        var auditRecorded = await WaitUntilAsync(
            () => CountCleaningAuditEntriesAsync(tenantId, cleaningId, "late_checkout_approved"), count => count > 0, TimeSpan.FromSeconds(30));
        auditRecorded.Should().BeTrue(
            "Housekeeping's own LateCheckoutApprovedCleaningReactor must record exactly one audit entry within 30s. Worker output:\n" +
            _fixture.GetWorkerOutputSnapshot());
        (await CountCleaningAuditEntriesAsync(tenantId, cleaningId, "late_checkout_approved")).Should().Be(1,
            "exactly one audit entry — the same event delivered to two consumers must never duplicate either one's own side effect");

        var cleaningAfterReaction = await GetCleaningAsync(tenantId, reservationId);
        cleaningAfterReaction!.Value.ScheduledAtUtc.Should().BeNull(
            "HousekeepingReactionObserved=true, CleaningScheduleMutationImplemented=false, Reason=NoDocumentedSchedulingRule — " +
            "the reaction must never invent a schedule offset with no rule defined in Documento 10");
    }

    // ==================================================================
    // 4. LATE CHECKOUT — PENDING PAYMENT (critical gate: prove NOTHING happens)
    // ==================================================================

    [Fact]
    public async Task LateCheckout_requiring_Pix_settles_at_PendingPayment_and_triggers_absolutely_no_downstream_effect()
    {
        var tenantId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var checkInAt = now.AddDays(5);
        var checkOutAt = now.AddDays(8);

        var propertyId = await SeedActivePropertyAsync(tenantId, now);
        var reservationId = await SeedConfirmedReservationAsync(tenantId, propertyId, checkInAt, checkOutAt);
        await WaitForGuestStayOperationStatusAsync(tenantId, reservationId, "Active");
        await CheckInGuestAsync(tenantId, reservationId);
        await WaitForGuestStayOperationStatusAsync(tenantId, reservationId, "CheckedIn");

        var cleaningExists = await WaitUntilAsync(
            () => GetCleaningAsync(tenantId, reservationId), cleaning => cleaning is not null, TimeSpan.FromSeconds(30));
        cleaningExists.Should().BeTrue();
        var cleaningId = (await GetCleaningAsync(tenantId, reservationId))!.Value.Id;

        await SeedLateCheckoutPolicyAsync(tenantId,
            """{"allowed":true,"latestTime":null,"chargeType":"fixedAmount","chargeValue":50.00,"requiresPix":true,"blocksCalendar":false,"updatesCleaning":true}""");

        var requestedCheckOutAt = checkOutAt.AddHours(3);
        var token = await GenerateAdminTokenAsync(tenantId);

        var response = await PostJsonAsync(
            $"/api/v1/guest-operations/reservations/{reservationId}/late-checkout",
            token, new { requestedCheckOutAt });

        response.StatusCode.Should().Be(HttpStatusCode.OK, await SafeReadBodyAsync(response));
        var body = await response.Content.ReadFromJsonAsync<LateCheckoutResponseShape>();
        body!.Status.Should().Be("pending_payment");
        body.RequiresPix.Should().BeTrue();
        body.ChargeValue.Should().Be(50.00m);

        var requestRow = await GetLatestLateCheckoutRequestAsync(tenantId, reservationId);
        requestRow.Status.Should().Be("PendingPayment");

        // ---- Deliberately no polling/waiting here: PendingPayment
        // publishes NOTHING (LateCheckoutApproved is only ever published for
        // a true, final approval — never this outcome), so there is nothing
        // asynchronous to wait for. A short, fixed settle delay proves the
        // ABSENCE of an effect that a poll-until-true could otherwise miss
        // by returning before a wrongly-fired side effect had time to land. ----
        await Task.Delay(TimeSpan.FromSeconds(5));

        var finalSchedule = await GetReservationScheduleAsync(tenantId, reservationId);
        finalSchedule.CheckOutAt.Should().BeCloseTo(checkOutAt, TimeSpan.FromSeconds(1),
            "PendingPayment must NEVER reschedule the real Reservation — Fase 10, Checkpoint 5 is what eventually resolves this");

        _fixture.GetWorkerOutputSnapshot().Should().NotContain($"Reservation rescheduled for late checkout, tenant {tenantId} reservationId {reservationId}",
            "Workflow must never have received a reschedule command for a PendingPayment outcome");

        (await CountCleaningAuditEntriesAsync(tenantId, cleaningId, "late_checkout_approved")).Should().Be(0,
            "Housekeeping must never react to a PendingPayment outcome — LateCheckoutApproved was never published");

        // Fase 10, Checkpoint 5 (PIX/Payment Deterministic Foundation) now
        // exists: PendingPayment DOES publish LateCheckoutPaymentRequired,
        // and the real Worker (Payments' own consumer, wired
        // unconditionally) reacts to it in the background during this test
        // — that reaction is Payments' own concern and is proven separately
        // by PixPaymentWorkflowRoundTripTests. This test's own assertions
        // above remain the CP3 invariant that still holds: PendingPayment
        // alone never reschedules the Reservation and never triggers
        // Housekeeping — only a LATER PixChargeConfirmed does.
    }

    // ==================================================================
    // 5. LATE CHECKOUT — PERCENTAGE (explicit unsupported functional failure)
    // ==================================================================

    [Fact]
    public async Task LateCheckout_with_a_Percentage_policy_fails_explicitly_before_persisting_any_row()
    {
        var tenantId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var checkInAt = now.AddDays(5);
        var checkOutAt = now.AddDays(8);

        var propertyId = await SeedActivePropertyAsync(tenantId, now);
        var reservationId = await SeedConfirmedReservationAsync(tenantId, propertyId, checkInAt, checkOutAt);
        await WaitForGuestStayOperationStatusAsync(tenantId, reservationId, "Active");
        await CheckInGuestAsync(tenantId, reservationId);
        await WaitForGuestStayOperationStatusAsync(tenantId, reservationId, "CheckedIn");

        await SeedLateCheckoutPolicyAsync(tenantId,
            """{"allowed":true,"latestTime":null,"chargeType":"percentage","chargeValue":10.00,"requiresPix":false,"blocksCalendar":false,"updatesCleaning":false}""");

        var requestedCheckOutAt = checkOutAt.AddHours(3);
        var token = await GenerateAdminTokenAsync(tenantId);

        var response = await PostJsonAsync(
            $"/api/v1/guest-operations/reservations/{reservationId}/late-checkout",
            token, new { requestedCheckOutAt });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict, await SafeReadBodyAsync(response));

        (await CountLateCheckoutRequestsAsync(tenantId, reservationId)).Should().Be(0,
            "RequestPersisted=false — no row can snapshot a charge type this aggregate refuses to hold; never invent a pricing calculation");

        var finalSchedule = await GetReservationScheduleAsync(tenantId, reservationId);
        finalSchedule.CheckOutAt.Should().BeCloseTo(checkOutAt, TimeSpan.FromSeconds(1), "ReservationChanged=false");

        _fixture.GetWorkerOutputSnapshot().Should().NotContain($"Reservation rescheduled for late checkout, tenant {tenantId} reservationId {reservationId}");
    }

    // ==================================================================
    // 6. LATE CHECKOUT — ACTIVE REQUEST CARDINALITY
    //    (Early has no reachable duplicate-while-Pending scenario through
    //    sequential real HTTP calls — evaluation is synchronous and always
    //    resolves to a terminal status within the SAME request/transaction
    //    before the response is returned, so a second real call can never
    //    observe a still-Pending first request. This is the mandate's own
    //    documented design, not a test-coverage gap — see
    //    EarlyCheckInRequestStatus's own doc comment. Late's PendingPayment
    //    is the one real, lasting active state this cardinality rule
    //    protects.)
    // ==================================================================

    [Fact]
    public async Task LateCheckout_second_request_while_the_first_is_PendingPayment_is_rejected_as_already_active()
    {
        var tenantId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var checkInAt = now.AddDays(5);
        var checkOutAt = now.AddDays(8);

        var propertyId = await SeedActivePropertyAsync(tenantId, now);
        var reservationId = await SeedConfirmedReservationAsync(tenantId, propertyId, checkInAt, checkOutAt);
        await WaitForGuestStayOperationStatusAsync(tenantId, reservationId, "Active");
        await CheckInGuestAsync(tenantId, reservationId);
        await WaitForGuestStayOperationStatusAsync(tenantId, reservationId, "CheckedIn");

        await SeedLateCheckoutPolicyAsync(tenantId,
            """{"allowed":true,"latestTime":null,"chargeType":"fixedAmount","chargeValue":50.00,"requiresPix":true,"blocksCalendar":false,"updatesCleaning":false}""");

        var token = await GenerateAdminTokenAsync(tenantId);
        var requestedCheckOutAt = checkOutAt.AddHours(3);

        var firstResponse = await PostJsonAsync(
            $"/api/v1/guest-operations/reservations/{reservationId}/late-checkout", token, new { requestedCheckOutAt });
        firstResponse.StatusCode.Should().Be(HttpStatusCode.OK, await SafeReadBodyAsync(firstResponse));
        (await firstResponse.Content.ReadFromJsonAsync<LateCheckoutResponseShape>())!.Status.Should().Be("pending_payment");

        var secondResponse = await PostJsonAsync(
            $"/api/v1/guest-operations/reservations/{reservationId}/late-checkout", token, new { requestedCheckOutAt = requestedCheckOutAt.AddHours(1) });

        secondResponse.StatusCode.Should().Be(HttpStatusCode.Conflict, await SafeReadBodyAsync(secondResponse));
        (await CountLateCheckoutRequestsAsync(tenantId, reservationId)).Should().Be(1,
            "a second request while the first is still PendingPayment (active) must never create a second row");
    }

    // ---- Helpers ----------------------------------------------------------

    private async Task<Guid> SeedActivePropertyAsync(Guid tenantId, DateTimeOffset now)
    {
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);
        await using var dbContext = CreatePropertyManagementDbContext(tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext.Database, tenantId);

        var address = Address.Create("59090-000", "Rua Exemplo", "100", null, "Ponta Negra", "Natal", "RN");
        var property = Property.Create(
            Guid.NewGuid(), tenantId, PropertyCode.Create($"TST-{Guid.NewGuid():N}"[..12]), "Test Property",
            capacity: 4, condominiumId: null, address, now);
        property.Activate(now);

        dbContext.Properties.Add(property);
        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();

        // Mirrors what a real PropertyCreated + PropertyActivated round trip
        // through Housekeeping's own PropertyProjectionSynchronizer would
        // have produced — inserted directly rather than published (the
        // Property itself is seeded directly above, not via a real command
        // dispatch that would publish those events) — same precedent as
        // CreateCleaningForReservationWorkflowRoundTripTests' own
        // SeedActivePropertyAsync. Without this, Housekeeping's real
        // CreateCleaningForReservationCommandHandler's own IsKnownActivePropertyAsync
        // guard never finds the property "known", and the real
        // ReservationCreated -> Workflow -> Housekeeping Cleaning
        // auto-creation choreography never completes.
        await using (var connection = new NpgsqlConnection(_fixture.MigratorConnectionString))
        {
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
        }

        return property.Id;
    }

    private async Task<Guid> SeedConfirmedReservationAsync(Guid tenantId, Guid propertyId, DateTimeOffset checkInAt, DateTimeOffset checkOutAt)
    {
        using var scope = _fixture.ApiServices.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(tenantId);
        var dispatcher = scope.ServiceProvider.GetRequiredService<IReservationsRequestDispatcher>();

        var result = await dispatcher.Send(new CreateReservationCommand(
            tenantId, Guid.NewGuid(), propertyId, "Test Guest", null, checkInAt, checkOutAt, GuestCount: 2));
        result.IsSuccess.Should().BeTrue("the seeded Property must be genuinely eligible for a new reservation");
        return result.Value.Id;
    }

    private async Task CheckInGuestAsync(Guid tenantId, Guid reservationId)
    {
        using var scope = _fixture.ApiServices.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(tenantId);
        var dispatcher = scope.ServiceProvider.GetRequiredService<IGuestOperationsRequestDispatcher>();

        var result = await dispatcher.Send(new RecordGuestCheckedInCommand { TenantId = tenantId, ReservationId = reservationId, ActorId = Guid.NewGuid() });
        result.IsSuccess.Should().BeTrue("the auto-created GuestStayOperation must be Active and therefore eligible for check-in");
    }

    private async Task SeedEarlyCheckInPolicyAsync(Guid tenantId, string jsonValue)
    {
        using var scope = _fixture.ApiServices.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(tenantId);
        var dispatcher = scope.ServiceProvider.GetRequiredService<IConfigurationRequestDispatcher>();

        var result = await dispatcher.Send(new CreatePolicyValueVersionCommand(
            tenantId, Guid.NewGuid(), "EARLY_CHECKIN", "Tenant", null, jsonValue, "E2E test setup", null, null));
        result.IsSuccess.Should().BeTrue("EARLY_CHECKIN policy seeding must succeed — this is real Configuration & Policy write, not a mock");
    }

    private async Task SeedLateCheckoutPolicyAsync(Guid tenantId, string jsonValue)
    {
        using var scope = _fixture.ApiServices.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(tenantId);
        var dispatcher = scope.ServiceProvider.GetRequiredService<IConfigurationRequestDispatcher>();

        var result = await dispatcher.Send(new CreatePolicyValueVersionCommand(
            tenantId, Guid.NewGuid(), "LATE_CHECKOUT", "Tenant", null, jsonValue, "E2E test setup", null, null));
        result.IsSuccess.Should().BeTrue("LATE_CHECKOUT policy seeding must succeed — this is real Configuration & Policy write, not a mock");
    }

    private async Task<string> GenerateAdminTokenAsync(Guid tenantId)
    {
        using var scope = _fixture.ApiServices.CreateScope();
        var generator = scope.ServiceProvider.GetRequiredService<IJwtTokenGenerator>();
        var request = new JwtAccessTokenRequest(UserId: Guid.NewGuid(), TenantId: tenantId, SessionId: Guid.NewGuid(), Roles: ["ADMIN"]);
        return generator.GenerateAccessToken(request).Token;
    }

    private async Task<HttpResponseMessage> PostJsonAsync(string route, string token, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, route)
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _fixture.ApiClient.SendAsync(request);
    }

    private static async Task<string> SafeReadBodyAsync(HttpResponseMessage response)
    {
        try { return await response.Content.ReadAsStringAsync(); }
        catch { return "(unreadable body)"; }
    }

    private async Task WaitForGuestStayOperationStatusAsync(Guid tenantId, Guid reservationId, string expectedStatus)
    {
        var reached = await WaitUntilAsync(
            () => GetGuestStayOperationStatusAsync(tenantId, reservationId), status => status == expectedStatus, TimeSpan.FromSeconds(30));
        reached.Should().BeTrue(
            $"the real ReservationCreated -> Guest Operations choreography must reach GuestStayOperation status '{expectedStatus}' within 30s. " +
            "Worker output:\n" + _fixture.GetWorkerOutputSnapshot());
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

    // ---- DB access ----------------------------------------------------------
    //
    // Every read below runs inside its own short transaction with a
    // transaction-scoped SET LOCAL app.tenant_id — mirrors
    // GuestCheckedOutCloseReservationWorkerRoundTripTests'
    // GetReservationStatusUnderTenantAsync exactly. SET LOCAL (never a bare
    // session-level SET) is deliberate: NpgsqlConnection pools physical
    // connections by connection string, and a session-level SET on a pooled
    // connection would leak the tenant setting into whichever test borrows
    // that same physical connection next — SET LOCAL is undone at COMMIT.

    private Task<string?> GetGuestStayOperationStatusAsync(Guid tenantId, Guid reservationId) =>
        QueryScopedAsync(tenantId, async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT status FROM guest_operations.guest_stay_operations WHERE tenant_id = @tenantId AND reservation_id = @reservationId";
            command.Parameters.AddWithValue("tenantId", tenantId);
            command.Parameters.AddWithValue("reservationId", reservationId);
            return (await command.ExecuteScalarAsync()) as string;
        });

    private Task<(string? Status, DateTimeOffset? CheckInAt, DateTimeOffset CheckOutAt)> GetReservationScheduleAsync(Guid tenantId, Guid reservationId) =>
        QueryScopedAsync(tenantId, async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT status, check_in_at, check_out_at FROM reservations.reservations WHERE tenant_id = @tenantId AND id = @id";
            command.Parameters.AddWithValue("tenantId", tenantId);
            command.Parameters.AddWithValue("id", reservationId);

            await using var reader = await command.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
                return (null, null, default);

            return ((string?)reader.GetString(0), (DateTimeOffset?)reader.GetFieldValue<DateTimeOffset>(1), reader.GetFieldValue<DateTimeOffset>(2));
        });

    private Task<(string Status, string? DenialReason)> GetLatestEarlyCheckInRequestAsync(Guid tenantId, Guid reservationId) =>
        QueryScopedAsync(tenantId, async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT status, denial_reason FROM guest_operations.early_check_in_requests
                WHERE tenant_id = @tenantId AND reservation_id = @reservationId
                ORDER BY created_at_utc DESC LIMIT 1
                """;
            command.Parameters.AddWithValue("tenantId", tenantId);
            command.Parameters.AddWithValue("reservationId", reservationId);

            await using var reader = await command.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
                throw new InvalidOperationException($"No early_check_in_requests row found for tenant {tenantId} reservation {reservationId}.");

            return (reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1));
        });

    private Task<(string Status, string? DenialReason, bool RequiresPix, decimal? ChargeValue)> GetLatestLateCheckoutRequestAsync(Guid tenantId, Guid reservationId) =>
        QueryScopedAsync(tenantId, async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT status, denial_reason, requires_pix, charge_value FROM guest_operations.late_checkout_requests
                WHERE tenant_id = @tenantId AND reservation_id = @reservationId
                ORDER BY created_at_utc DESC LIMIT 1
                """;
            command.Parameters.AddWithValue("tenantId", tenantId);
            command.Parameters.AddWithValue("reservationId", reservationId);

            await using var reader = await command.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
                throw new InvalidOperationException($"No late_checkout_requests row found for tenant {tenantId} reservation {reservationId}.");

            return (
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetBoolean(2),
                reader.IsDBNull(3) ? (decimal?)null : reader.GetDecimal(3));
        });

    private Task<int> CountLateCheckoutRequestsAsync(Guid tenantId, Guid reservationId) =>
        QueryScopedAsync(tenantId, async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM guest_operations.late_checkout_requests WHERE tenant_id = @tenantId AND reservation_id = @reservationId";
            command.Parameters.AddWithValue("tenantId", tenantId);
            command.Parameters.AddWithValue("reservationId", reservationId);
            return Convert.ToInt32(await command.ExecuteScalarAsync());
        });

    private Task<(Guid Id, string Status, DateTimeOffset? ScheduledAtUtc)?> GetCleaningAsync(Guid tenantId, Guid reservationId) =>
        QueryScopedAsync(tenantId, async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT id, status, scheduled_at_utc FROM housekeeping.cleanings WHERE tenant_id = @tenantId AND reservation_id = @reservationId";
            command.Parameters.AddWithValue("tenantId", tenantId);
            command.Parameters.AddWithValue("reservationId", reservationId);

            await using var reader = await command.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
                return ((Guid Id, string Status, DateTimeOffset? ScheduledAtUtc)?)null;

            return (reader.GetGuid(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetFieldValue<DateTimeOffset>(2));
        });

    private Task<int> CountCleaningAuditEntriesAsync(Guid tenantId, Guid cleaningId, string actionCode) =>
        QueryScopedAsync(tenantId, async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM housekeeping.cleaning_audit_log WHERE tenant_id = @tenantId AND aggregate_id = @cleaningId AND action_code = @actionCode";
            command.Parameters.AddWithValue("tenantId", tenantId);
            command.Parameters.AddWithValue("cleaningId", cleaningId);
            command.Parameters.AddWithValue("actionCode", actionCode);
            return Convert.ToInt32(await command.ExecuteScalarAsync());
        });

    private async Task<T> QueryScopedAsync<T>(Guid tenantId, Func<NpgsqlConnection, Task<T>> query)
    {
        await using var connection = new NpgsqlConnection(_fixture.MigratorConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var setCommand = connection.CreateCommand())
        {
            setCommand.CommandText = $"SET LOCAL app.tenant_id = '{tenantId:D}'";
            await setCommand.ExecuteNonQueryAsync();
        }

        var result = await query(connection);
        await transaction.CommitAsync();
        return result;
    }

    private static async Task SetTenantAsync(Microsoft.EntityFrameworkCore.Infrastructure.DatabaseFacade database, Guid tenantId) =>
        await database.ExecuteSqlInterpolatedAsync($"SELECT set_config('app.tenant_id', {tenantId.ToString()}, true)");

    private PropertyManagementDbContext CreatePropertyManagementDbContext(ITenantContext tenantContext)
    {
        var options = new DbContextOptionsBuilder<PropertyManagementDbContext>()
            .UseNpgsql(_fixture.MigratorConnectionString, npgsqlOptions => npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "property_management"))
            .Options;
        return new PropertyManagementDbContext(options, tenantContext);
    }

    private sealed record EarlyCheckInResponseShape(
        Guid Id, Guid ReservationId, DateTimeOffset RequestedCheckInAt, string Status, string? DenialReasonCode,
        DateTimeOffset CreatedAtUtc, DateTimeOffset? DecidedAtUtc, DateTimeOffset UpdatedAtUtc);

    private sealed record LateCheckoutResponseShape(
        Guid Id, Guid ReservationId, DateTimeOffset RequestedCheckOutAt, string ChargeType, decimal? ChargeValue, bool RequiresPix,
        string Status, string? DenialReasonCode, DateTimeOffset CreatedAtUtc, DateTimeOffset? DecidedAtUtc, DateTimeOffset UpdatedAtUtc);
}
