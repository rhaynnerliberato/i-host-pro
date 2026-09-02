using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using FluentAssertions;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.Contexts.Configuration.Application;
using IHostPro.Contexts.Configuration.Application.Policies;
using IHostPro.Contexts.GuestOperations.Application;
using IHostPro.Contexts.Identity.Application;
using IHostPro.Contexts.Identity.Infrastructure.Authentication;
using IHostPro.Contexts.PropertyManagement.Application;
using IHostPro.Contexts.PropertyManagement.Application.FrontDesk;
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
/// Real end-to-end proof of Fase 10, Checkpoint 4 (Portaria Notification
/// Foundation) — mandate §39-41: the mandatory gate, no unit/integration
/// test in isolation is accepted as sufficient. Every scenario runs against
/// a real Postgres instance, a real RabbitMQ broker, a real unmodified
/// <c>IHostPro.Worker.dll</c> subprocess, and the real HTTP surface of
/// <c>IHostPro.Api</c> — mirrors <c>EarlyCheckInLateCheckoutWorkflowRoundTripTests</c>'
/// own infrastructure exactly (own copy of the Fixture, since each heavy
/// E2E file in this project owns its own container/subprocess set —
/// established precedent, not an oversight).
///
/// <see cref="IFrontDeskContactReader"/> is never faked here — every
/// resolution runs the REAL Infrastructure implementation (ADR-026),
/// against the real seeded Postgres data. <c>FakeWhatsAppConnector</c> is
/// the one and only <c>IOutboundMessageConnector</c> active in this fixture
/// (Development-gated, same precedent as CP1/CP2) — zero real WhatsApp
/// provider is ever called, proven by the total absence of any
/// ExternalIntegrations connection string requirement for message dispatch.
/// </summary>
public sealed class FrontDeskNotificationWorkflowRoundTripTests : IClassFixture<FrontDeskNotificationWorkflowRoundTripTests.Fixture>
{
    private readonly Fixture _fixture;

    public FrontDeskNotificationWorkflowRoundTripTests(Fixture fixture) => _fixture = fixture;

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
                "communication.guest-checked-in-trigger",
                "communication.early-checkin-approved-trigger",
                "communication.late-checkout-approved-trigger",
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
                try { _workerProcess.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { /* Already exited between the check and Kill. */ }
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
    // 1. GUEST CHECKED IN — FRONT DESK CONTACT CONFIGURED
    //    Proves the recipient is the FRONT DESK, never the guest.
    // ==================================================================

    [Fact]
    public async Task GuestCheckedIn_with_a_configured_front_desk_contact_creates_a_real_Message_addressed_to_the_front_desk_not_the_guest()
    {
        var tenantId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var checkInAt = now.AddDays(5);
        var checkOutAt = now.AddDays(8);
        const string guestPhone = "+5511999998888";
        const string frontDeskPhone = "+5511977776666";

        var condominiumId = await SeedCondominiumAsync(tenantId, now);
        var propertyId = await SeedActivePropertyAsync(tenantId, now, condominiumId);
        await SeedFrontDeskContactAsync(tenantId, condominiumId, "Portaria Bloco A", frontDeskPhone);
        await SeedTemplateAsync(tenantId, "FRONT_DESK_GUEST_CHECKED_IN", "Hospede {{GuestName}} chegou em {{CheckedInAt}}");

        var reservationId = await SeedConfirmedReservationAsync(tenantId, propertyId, checkInAt, checkOutAt, guestPhone);
        await WaitForGuestStayOperationStatusAsync(tenantId, reservationId, "Active");

        await CheckInGuestAsync(tenantId, reservationId);
        await WaitForGuestStayOperationStatusAsync(tenantId, reservationId, "CheckedIn");

        var messageCreated = await WaitUntilAsync(
            () => GetMessageAsync(tenantId, reservationId, "FRONT_DESK_GUEST_CHECKED_IN"), m => m is not null, TimeSpan.FromSeconds(30));
        messageCreated.Should().BeTrue(
            "the real GuestCheckedIn -> Communication chain must create a Message within 30s. Worker output:\n" + _fixture.GetWorkerOutputSnapshot());

        var message = (await GetMessageAsync(tenantId, reservationId, "FRONT_DESK_GUEST_CHECKED_IN"))!.Value;
        message.Status.Should().Be("Sent", "FakeWhatsAppConnector always succeeds");
        message.DestinationMasked.Should().EndWith("6666", "the recipient must be the front desk phone, ending in the front desk's own last 4 digits");
        message.DestinationMasked.Should().NotContain(guestPhone).And.NotContain(frontDeskPhone, "only a masked reference is ever persisted");
    }

    // ==================================================================
    // 2. GUEST CHECKED IN — NO FRONT DESK CONTACT CONFIGURED
    //    Deliberate no-op: zero Message, pipeline stays green.
    // ==================================================================

    [Fact]
    public async Task GuestCheckedIn_without_a_front_desk_contact_is_a_deliberate_no_op()
    {
        var tenantId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var checkInAt = now.AddDays(5);
        var checkOutAt = now.AddDays(8);

        // No Condominium, no FrontDeskContact seeded at all — a standalone Property.
        var propertyId = await SeedActivePropertyAsync(tenantId, now, condominiumId: null);
        await SeedTemplateAsync(tenantId, "FRONT_DESK_GUEST_CHECKED_IN", "Hospede {{GuestName}} chegou em {{CheckedInAt}}");

        var reservationId = await SeedConfirmedReservationAsync(tenantId, propertyId, checkInAt, checkOutAt, "+5511999998888");
        await WaitForGuestStayOperationStatusAsync(tenantId, reservationId, "Active");

        await CheckInGuestAsync(tenantId, reservationId);
        await WaitForGuestStayOperationStatusAsync(tenantId, reservationId, "CheckedIn");

        // No polling for absence — a short, fixed settle delay, same
        // reasoning as the PendingPayment CP3 gate: proves the ABSENCE of an
        // effect that a poll-until-true could otherwise miss.
        await Task.Delay(TimeSpan.FromSeconds(5));

        (await GetMessageAsync(tenantId, reservationId, "FRONT_DESK_GUEST_CHECKED_IN")).Should().BeNull(
            "no FrontDeskContact configured must never create a Message — mandate §19, deliberate no-op");

        _fixture.GetWorkerOutputSnapshot().Should().Contain("FrontDeskContactNotConfigured");
    }

    // ==================================================================
    // 3. EARLY CHECK-IN APPROVED — FAN-OUT (Workflow + Communication)
    // ==================================================================

    [Fact]
    public async Task EarlyCheckinApproved_fans_out_to_both_Workflow_and_Communication()
    {
        var tenantId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var checkInAt = now.AddDays(5);
        var checkOutAt = now.AddDays(8);
        const string frontDeskPhone = "+5511977776666";

        var condominiumId = await SeedCondominiumAsync(tenantId, now);
        var propertyId = await SeedActivePropertyAsync(tenantId, now, condominiumId);
        await SeedFrontDeskContactAsync(tenantId, condominiumId, "Portaria Bloco A", frontDeskPhone);
        await SeedTemplateAsync(tenantId, "FRONT_DESK_EARLY_CHECKIN_APPROVED", "Check-in antecipado de {{GuestName}} para {{ApprovedCheckInAt}}");

        var reservationId = await SeedConfirmedReservationAsync(tenantId, propertyId, checkInAt, checkOutAt, "+5511999998888");
        await WaitForGuestStayOperationStatusAsync(tenantId, reservationId, "Active");

        await SeedEarlyCheckInPolicyAsync(tenantId,
            """{"allowed":true,"earliestTime":null,"requiresCleaningCompleted":false,"requiresForm":false,"notifyFrontDesk":false}""");

        var requestedCheckInAt = checkInAt.AddHours(-2);
        var token = await GenerateAdminTokenAsync(tenantId);

        var response = await PostJsonAsync(
            $"/api/v1/guest-operations/reservations/{reservationId}/early-check-in", token, new { requestedCheckInAt });
        response.StatusCode.Should().Be(HttpStatusCode.OK, await SafeReadBodyAsync(response));

        // Workflow's own reschedule (already proven end-to-end by CP3 — here
        // it re-confirms the SAME event still reaches Workflow after adding
        // Communication as a second consumer, ADR-020 isolation).
        var rescheduled = await WaitUntilAsync(
            () => GetReservationCheckInAtAsync(tenantId, reservationId),
            value => value is not null && Math.Abs((value.Value - requestedCheckInAt).TotalSeconds) < 1,
            TimeSpan.FromSeconds(30));
        rescheduled.Should().BeTrue("Workflow must still reschedule the real Reservation after Communication was added as a second consumer. Worker output:\n" + _fixture.GetWorkerOutputSnapshot());

        // Communication's own, SEPARATE reaction to the SAME event.
        var messageCreated = await WaitUntilAsync(
            () => GetMessageAsync(tenantId, reservationId, "FRONT_DESK_EARLY_CHECKIN_APPROVED"), m => m is not null, TimeSpan.FromSeconds(30));
        messageCreated.Should().BeTrue("Communication's own Front Desk processor must independently react to the same EarlyCheckinApproved. Worker output:\n" + _fixture.GetWorkerOutputSnapshot());

        (await GetMessageAsync(tenantId, reservationId, "FRONT_DESK_EARLY_CHECKIN_APPROVED"))!.Value.Status.Should().Be("Sent");
    }

    // ==================================================================
    // 4. LATE CHECKOUT APPROVED — FAN-OUT (Workflow + Housekeeping + Communication)
    // ==================================================================

    [Fact]
    public async Task LateCheckoutApproved_fans_out_to_Workflow_Housekeeping_and_Communication()
    {
        var tenantId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var checkInAt = now.AddDays(5);
        var checkOutAt = now.AddDays(8);
        const string frontDeskPhone = "+5511977776666";

        var condominiumId = await SeedCondominiumAsync(tenantId, now);
        var propertyId = await SeedActivePropertyAsync(tenantId, now, condominiumId);
        await SeedFrontDeskContactAsync(tenantId, condominiumId, "Portaria Bloco A", frontDeskPhone);
        await SeedTemplateAsync(tenantId, "FRONT_DESK_LATE_CHECKOUT_APPROVED", "Checkout tardio de {{GuestName}} ate {{ApprovedCheckOutAt}}");

        var reservationId = await SeedConfirmedReservationAsync(tenantId, propertyId, checkInAt, checkOutAt, "+5511999998888");
        await WaitForGuestStayOperationStatusAsync(tenantId, reservationId, "Active");
        await CheckInGuestAsync(tenantId, reservationId);
        await WaitForGuestStayOperationStatusAsync(tenantId, reservationId, "CheckedIn");

        var cleaningExists = await WaitUntilAsync(
            () => GetCleaningIdAsync(tenantId, reservationId), id => id is not null, TimeSpan.FromSeconds(30));
        cleaningExists.Should().BeTrue("the real ReservationCreated -> Workflow -> Housekeeping choreography must auto-create a Cleaning within 30s");
        var cleaningId = (await GetCleaningIdAsync(tenantId, reservationId))!.Value;

        await SeedLateCheckoutPolicyAsync(tenantId,
            """{"allowed":true,"latestTime":null,"chargeType":"none","chargeValue":null,"requiresPix":false,"blocksCalendar":false,"updatesCleaning":true}""");

        var requestedCheckOutAt = checkOutAt.AddHours(3);
        var token = await GenerateAdminTokenAsync(tenantId);

        var response = await PostJsonAsync(
            $"/api/v1/guest-operations/reservations/{reservationId}/late-checkout", token, new { requestedCheckOutAt });
        response.StatusCode.Should().Be(HttpStatusCode.OK, await SafeReadBodyAsync(response));

        var rescheduled = await WaitUntilAsync(
            () => GetReservationCheckOutAtAsync(tenantId, reservationId),
            value => Math.Abs((value - requestedCheckOutAt).TotalSeconds) < 1,
            TimeSpan.FromSeconds(30));
        rescheduled.Should().BeTrue("Workflow must still reschedule after Communication was added as a third consumer. Worker output:\n" + _fixture.GetWorkerOutputSnapshot());

        var auditRecorded = await WaitUntilAsync(
            () => CountCleaningAuditEntriesAsync(tenantId, cleaningId, "late_checkout_approved"), count => count > 0, TimeSpan.FromSeconds(30));
        auditRecorded.Should().BeTrue("Housekeeping must still react after Communication was added as a third consumer. Worker output:\n" + _fixture.GetWorkerOutputSnapshot());
        (await CountCleaningAuditEntriesAsync(tenantId, cleaningId, "late_checkout_approved")).Should().Be(1,
            "exactly one audit entry — adding a third consumer must never duplicate Housekeeping's own side effect");

        var messageCreated = await WaitUntilAsync(
            () => GetMessageAsync(tenantId, reservationId, "FRONT_DESK_LATE_CHECKOUT_APPROVED"), m => m is not null, TimeSpan.FromSeconds(30));
        messageCreated.Should().BeTrue("Communication's own Front Desk processor must independently react. Worker output:\n" + _fixture.GetWorkerOutputSnapshot());
        (await GetMessageAsync(tenantId, reservationId, "FRONT_DESK_LATE_CHECKOUT_APPROVED"))!.Value.Status.Should().Be("Sent");
    }

    // ---- Helpers ----------------------------------------------------------

    private async Task<Guid> SeedCondominiumAsync(Guid tenantId, DateTimeOffset now)
    {
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);
        await using var dbContext = CreatePropertyManagementDbContext(tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext.Database, tenantId);

        var address = Address.Create("59090-000", "Rua Exemplo", "100", null, "Ponta Negra", "Natal", "RN");
        var condominium = Condominium.Create(Guid.NewGuid(), tenantId, "Test Condominium", address, now);
        dbContext.Condominiums.Add(condominium);

        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();

        return condominium.Id;
    }

    private async Task<Guid> SeedActivePropertyAsync(Guid tenantId, DateTimeOffset now, Guid? condominiumId)
    {
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);
        await using var dbContext = CreatePropertyManagementDbContext(tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext.Database, tenantId);

        var address = Address.Create("59090-000", "Rua Exemplo", "100", null, "Ponta Negra", "Natal", "RN");
        var property = Property.Create(
            Guid.NewGuid(), tenantId, PropertyCode.Create($"TST-{Guid.NewGuid():N}"[..12]), "Test Property",
            capacity: 4, condominiumId, address, now);
        property.Activate(now);

        dbContext.Properties.Add(property);
        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();

        // Mirrors CreateCleaningForReservationWorkflowRoundTripTests'/
        // EarlyCheckInLateCheckoutWorkflowRoundTripTests' own
        // SeedActivePropertyAsync precedent — Housekeeping's real
        // CreateCleaningForReservationCommandHandler needs its own local
        // property_projection synced for the Cleaning auto-creation
        // choreography to complete.
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

    private async Task SeedFrontDeskContactAsync(Guid tenantId, Guid condominiumId, string displayName, string phoneNumber)
    {
        using var scope = _fixture.ApiServices.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(tenantId);
        var dispatcher = scope.ServiceProvider.GetRequiredService<IPropertyManagementRequestDispatcher>();

        var result = await dispatcher.Send(new SetFrontDeskContactCommand(tenantId, Guid.NewGuid(), condominiumId, displayName, phoneNumber, IsActive: true));
        result.IsSuccess.Should().BeTrue("front desk contact seeding must succeed — this is a real Property Management write, not a mock");
    }

    private async Task SeedTemplateAsync(Guid tenantId, string key, string content)
    {
        // Configuration.Api's own TemplatesController creates via HTTP in
        // production; in-process command dispatch is used here purely for
        // deterministic test setup, mirroring SeedEarlyCheckInPolicyAsync's
        // own precedent (CP3) exactly. Content is deliberately generic
        // test copy — never real production message text (mandate §17: "não
        // criar conteúdo textual arbitrário se source of truth não define
        // copy").
        await using var connection = new NpgsqlConnection(_fixture.MigratorConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var setCommand = connection.CreateCommand())
        {
            setCommand.CommandText = $"SET LOCAL app.tenant_id = '{tenantId:D}'";
            await setCommand.ExecuteNonQueryAsync();
        }

        await using (var insertCommand = connection.CreateCommand())
        {
            insertCommand.CommandText = """
                INSERT INTO configuration.templates (id, tenant_id, key, content, is_active, created_at_utc, updated_at_utc)
                VALUES (@id, @tenantId, @key, @content, true, now(), now())
                """;
            insertCommand.Parameters.AddWithValue("id", Guid.NewGuid());
            insertCommand.Parameters.AddWithValue("tenantId", tenantId);
            insertCommand.Parameters.AddWithValue("key", key);
            insertCommand.Parameters.AddWithValue("content", content);
            await insertCommand.ExecuteNonQueryAsync();
        }
        await transaction.CommitAsync();
    }

    private async Task<Guid> SeedConfirmedReservationAsync(Guid tenantId, Guid propertyId, DateTimeOffset checkInAt, DateTimeOffset checkOutAt, string guestPhone)
    {
        using var scope = _fixture.ApiServices.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(tenantId);
        var dispatcher = scope.ServiceProvider.GetRequiredService<IReservationsRequestDispatcher>();

        var result = await dispatcher.Send(new CreateReservationCommand(
            tenantId, Guid.NewGuid(), propertyId, "Ana Silva", guestPhone, checkInAt, checkOutAt, GuestCount: 2));
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
        result.IsSuccess.Should().BeTrue();
    }

    private async Task SeedLateCheckoutPolicyAsync(Guid tenantId, string jsonValue)
    {
        using var scope = _fixture.ApiServices.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(tenantId);
        var dispatcher = scope.ServiceProvider.GetRequiredService<IConfigurationRequestDispatcher>();

        var result = await dispatcher.Send(new CreatePolicyValueVersionCommand(
            tenantId, Guid.NewGuid(), "LATE_CHECKOUT", "Tenant", null, jsonValue, "E2E test setup", null, null));
        result.IsSuccess.Should().BeTrue();
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
        var request = new HttpRequestMessage(HttpMethod.Post, route) { Content = JsonContent.Create(body) };
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

    // ---- DB access ----

    private Task<string?> GetGuestStayOperationStatusAsync(Guid tenantId, Guid reservationId) =>
        QueryScopedAsync(tenantId, async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT status FROM guest_operations.guest_stay_operations WHERE tenant_id = @tenantId AND reservation_id = @reservationId";
            command.Parameters.AddWithValue("tenantId", tenantId);
            command.Parameters.AddWithValue("reservationId", reservationId);
            return (await command.ExecuteScalarAsync()) as string;
        });

    private Task<DateTimeOffset?> GetReservationCheckInAtAsync(Guid tenantId, Guid reservationId) =>
        QueryScopedAsync(tenantId, async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT check_in_at FROM reservations.reservations WHERE tenant_id = @tenantId AND id = @id";
            command.Parameters.AddWithValue("tenantId", tenantId);
            command.Parameters.AddWithValue("id", reservationId);
            await using var reader = await command.ExecuteReaderAsync();
            if (!await reader.ReadAsync() || reader.IsDBNull(0))
                return (DateTimeOffset?)null;
            return reader.GetFieldValue<DateTimeOffset>(0);
        });

    private Task<DateTimeOffset> GetReservationCheckOutAtAsync(Guid tenantId, Guid reservationId) =>
        QueryScopedAsync(tenantId, async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT check_out_at FROM reservations.reservations WHERE tenant_id = @tenantId AND id = @id";
            command.Parameters.AddWithValue("tenantId", tenantId);
            command.Parameters.AddWithValue("id", reservationId);
            await using var reader = await command.ExecuteReaderAsync();
            await reader.ReadAsync();
            return reader.GetFieldValue<DateTimeOffset>(0);
        });

    private Task<Guid?> GetCleaningIdAsync(Guid tenantId, Guid reservationId) =>
        QueryScopedAsync(tenantId, async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT id FROM housekeeping.cleanings WHERE tenant_id = @tenantId AND reservation_id = @reservationId";
            command.Parameters.AddWithValue("tenantId", tenantId);
            command.Parameters.AddWithValue("reservationId", reservationId);
            var value = await command.ExecuteScalarAsync();
            return value is DBNull or null ? null : (Guid?)(Guid)value;
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

    private Task<(string Status, string? DestinationMasked)?> GetMessageAsync(Guid tenantId, Guid reservationId, string templateKey) =>
        QueryScopedAsync(tenantId, async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT status, destination_masked FROM communication.messages
                WHERE tenant_id = @tenantId AND reservation_id = @reservationId AND template_key = @templateKey
                """;
            command.Parameters.AddWithValue("tenantId", tenantId);
            command.Parameters.AddWithValue("reservationId", reservationId);
            command.Parameters.AddWithValue("templateKey", templateKey);

            await using var reader = await command.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
                return ((string Status, string? DestinationMasked)?)null;

            return (reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1));
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
}
