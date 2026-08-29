using System.Diagnostics;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.Contexts.Communication.Domain;
using IHostPro.Contexts.Communication.Infrastructure.Persistence;
using IHostPro.Contexts.ExternalIntegrations.Domain;
using IHostPro.Contexts.ExternalIntegrations.Infrastructure.Persistence;
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
/// Fase 11, Checkpoint 1 (Inbound Conversation Foundation) — mandatory real
/// transport E2E gate (mandate items 39-42). A signed WhatsApp inbound-message
/// webhook, received by the real <c>IHostPro.Api</c> pipeline (real signature
/// verification/route resolution/message normalization, unchanged webhook
/// security from ADR-022) → the real durable outbox → real RabbitMQ → a
/// real, unmodified <c>IHostPro.Worker.dll</c> subprocess → Communication's
/// own keyed Wolverine consumer (<c>InboundGuestMessageProcessor</c>) →
/// ADR-029's real <c>IReservationByGuestPhoneReader</c> against real
/// Reservations data → a persisted <see cref="Conversation"/> and inbound
/// <see cref="Message"/>. Never simulates Meta itself — only the HTTP
/// request/signature Meta would send. Mirrors
/// <c>GuestAccessDeliveryWorkflowRoundTripTests</c>'s dual-process
/// infrastructure exactly.
///
/// Proves, per Fact: exactly one Conversation/Message for a single eligible
/// Reservation (item 39); zero Conversation for zero eligible Reservations
/// (item 40); zero Conversation — never an auto-selected one — for multiple
/// eligible Reservations (item 41); exactly one persisted Message when the
/// same Meta message id is delivered twice (item 42, idempotency).
/// </summary>
public sealed class InboundGuestMessageWorkflowRoundTripTests : IClassFixture<InboundGuestMessageWorkflowRoundTripTests.Fixture>
{
    private const string PhoneNumberId = "e2e-inbound-phone-number-id";
    private const string AppSecret = "e2e-inbound-test-app-secret";
    private const string VerifyToken = "e2e-inbound-test-verify-token";

    private readonly Fixture _fixture;

    public InboundGuestMessageWorkflowRoundTripTests(Fixture fixture) => _fixture = fixture;

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

            var listening = await WaitForWorkerLogLineAsync(
                "Started message listening at rabbitmq://queue/communication.inbound-guest-message-trigger", TimeSpan.FromSeconds(45));
            if (!listening)
            {
                string snapshot;
                lock (_workerOutputLock) snapshot = _workerOutput.ToString();
                throw new InvalidOperationException($"Worker never reported listening to communication.inbound-guest-message-trigger. Worker output:\n{snapshot}");
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

            await SeedTenantRouteAsync();
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

        private async Task SeedTenantRouteAsync()
        {
            var options = new DbContextOptionsBuilder<ExternalIntegrationsDbContext>()
                .UseNpgsql(MigratorConnectionString, npgsqlOptions => npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "external_integrations"))
                .Options;
            await using var dbContext = new ExternalIntegrationsDbContext(options, new TenantContext());
            dbContext.WhatsAppTenantRoutes.Add(WhatsAppTenantRoute.Create(Guid.NewGuid(), PhoneNumberId, GlobalTenantId, DateTimeOffset.UtcNow));
            await dbContext.SaveChangesAsync();
        }

        // ---- Worker subprocess ----

        private readonly StringBuilder _workerOutput = new();
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
            ["OpenTelemetry__OtlpEndpoint"] = "http://127.0.0.1:14323",
        };

        private Dictionary<string, string?> BuildApiEnvironment(string signingKeyPem)
        {
            var values = new Dictionary<string, string?>();
            foreach (var (key, value) in BuildWorkerEnvironment(signingKeyPem))
                values[key] = value;
            values["ExternalIntegrations__WhatsApp__Webhook__AppSecret"] = AppSecret;
            values["ExternalIntegrations__WhatsApp__Webhook__VerifyToken"] = VerifyToken;
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

    // Every scenario in this class shares one tenant/one WhatsAppTenantRoute
    // (seeded once by the Fixture) — each [Fact] uses its own phone number so
    // Reservation-resolution candidates never leak across scenarios.
    private static readonly Guid GlobalTenantId = Guid.NewGuid();

    [Fact]
    public async Task A_signed_text_message_from_a_single_eligible_reservations_phone_creates_one_conversation_and_message()
    {
        const string guestPhone = "+5511911112222";
        var reservationId = await SeedConfirmedReservationAsync(guestPhone);

        var response = await SendInboundMessageAsync("wamid.E2E-SINGLE", "5511911112222", "text", "Olá, preciso de ajuda");
        response.EnsureSuccessStatusCode();

        var message = await WaitForInboundMessageAsync(GlobalTenantId, "wamid.E2E-SINGLE", TimeSpan.FromSeconds(30));
        message.Should().NotBeNull("the real InboundGuestMessageReceived -> Communication chain must persist the inbound Message within 30s. " +
            "Worker output:\n" + _fixture.GetWorkerOutputSnapshot());

        message!.ReservationId.Should().Be(reservationId);
        message.Direction.Should().Be(MessageDirection.Inbound);
        message.Status.Should().Be(MessageStatus.Received);
        message.RenderedContent.Should().Be("Olá, preciso de ajuda");

        var conversationCount = await CountConversationsAsync(GlobalTenantId, reservationId);
        conversationCount.Should().Be(1);
    }

    [Fact]
    public async Task A_signed_text_message_from_a_phone_with_zero_reservations_creates_no_conversation()
    {
        var response = await SendInboundMessageAsync("wamid.E2E-ZERO", "5511900000001", "text", "oi");
        response.EnsureSuccessStatusCode();

        // No Reservation was ever seeded for this phone — settle window, then assert absence.
        await Task.Delay(TimeSpan.FromSeconds(5));

        (await CountMessagesByProviderIdAsync(GlobalTenantId, "wamid.E2E-ZERO")).Should().Be(0,
            "0-reservation resolution must never create a Message/Conversation. Worker output:\n" + _fixture.GetWorkerOutputSnapshot());
    }

    [Fact]
    public async Task A_signed_text_message_from_a_phone_with_multiple_reservations_never_auto_selects_one()
    {
        const string guestPhone = "+5511933334444";
        await SeedConfirmedReservationAsync(guestPhone);
        await SeedConfirmedReservationAsync(guestPhone);

        var response = await SendInboundMessageAsync("wamid.E2E-MULTI", "5511933334444", "text", "oi");
        response.EnsureSuccessStatusCode();

        await Task.Delay(TimeSpan.FromSeconds(5));

        (await CountMessagesByProviderIdAsync(GlobalTenantId, "wamid.E2E-MULTI")).Should().Be(0,
            "N-reservation resolution must never auto-select one. Worker output:\n" + _fixture.GetWorkerOutputSnapshot());
    }

    [Fact]
    public async Task The_same_provider_message_id_delivered_twice_persists_exactly_one_message()
    {
        const string guestPhone = "+5511955556666";
        await SeedConfirmedReservationAsync(guestPhone);

        for (var i = 0; i < 2; i++)
        {
            var response = await SendInboundMessageAsync("wamid.E2E-DUP", "5511955556666", "text", "oi de novo");
            response.EnsureSuccessStatusCode();
        }

        var message = await WaitForInboundMessageAsync(GlobalTenantId, "wamid.E2E-DUP", TimeSpan.FromSeconds(30));
        message.Should().NotBeNull("Worker output:\n" + _fixture.GetWorkerOutputSnapshot());

        await Task.Delay(TimeSpan.FromSeconds(5));

        (await CountMessagesByProviderIdAsync(GlobalTenantId, "wamid.E2E-DUP")).Should().Be(1,
            "a redelivered Meta webhook must never create a second inbound Message");
    }

    // ---- Helpers ----------------------------------------------------------

    private async Task<HttpResponseMessage> SendInboundMessageAsync(string providerMessageId, string from, string type, string? text)
    {
        var textPart = text is null ? "" : ",\"text\":{\"body\":\"" + text + "\"}";
        var body = "{\"entry\":[{\"changes\":[{\"value\":{" +
            "\"metadata\":{\"phone_number_id\":\"" + PhoneNumberId + "\"}," +
            "\"messages\":[{\"id\":\"" + providerMessageId + "\",\"from\":\"" + from + "\",\"type\":\"" + type + "\"," +
            "\"timestamp\":\"" + DateTimeOffset.UtcNow.ToUnixTimeSeconds() + "\"" + textPart + "}]" +
            "}}]}]}";

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/integrations/whatsapp/webhook")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("X-Hub-Signature-256", ComputeSignature(body, AppSecret));
        return await _fixture.ApiClient.SendAsync(request);
    }

    private static string ComputeSignature(string body, string appSecret)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(appSecret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(body));
        return "sha256=" + Convert.ToHexString(hash).ToLowerInvariant();
    }

    private async Task<Guid> SeedConfirmedReservationAsync(string guestPhone)
    {
        using var scope = _fixture.ApiServices.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(GlobalTenantId);
        var dispatcher = scope.ServiceProvider.GetRequiredService<IReservationsRequestDispatcher>();

        var now = DateTimeOffset.UtcNow;
        var result = await dispatcher.Send(new CreateReservationCommand(
            GlobalTenantId, Guid.NewGuid(), await SeedActivePropertyAsync(), "Test Guest", guestPhone,
            now.AddDays(1), now.AddDays(3), GuestCount: 2));
        result.IsSuccess.Should().BeTrue("the seeded Property must be genuinely eligible for a new reservation");
        return result.Value.Id;
    }

    private async Task<Guid> SeedActivePropertyAsync()
    {
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(GlobalTenantId);
        await using var propertyDbContext = new IHostPro.Contexts.PropertyManagement.Infrastructure.Persistence.PropertyManagementDbContext(
            new DbContextOptionsBuilder<IHostPro.Contexts.PropertyManagement.Infrastructure.Persistence.PropertyManagementDbContext>()
                .UseNpgsql(_fixture.MigratorConnectionString, npgsqlOptions => npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "property_management"))
                .Options,
            tenantContext);
        await using var transaction = await propertyDbContext.Database.BeginTransactionAsync();
        await propertyDbContext.Database.ExecuteSqlInterpolatedAsync($"SELECT set_config('app.tenant_id', {GlobalTenantId.ToString()}, true)");

        var now = DateTimeOffset.UtcNow;
        var address = IHostPro.Contexts.PropertyManagement.Domain.ValueObjects.Address.Create("59090-000", "Rua Exemplo", "100", null, "Ponta Negra", "Natal", "RN");
        var property = IHostPro.Contexts.PropertyManagement.Domain.Property.Create(
            Guid.NewGuid(), GlobalTenantId, IHostPro.Contexts.PropertyManagement.Domain.ValueObjects.PropertyCode.Create($"E2E-{Guid.NewGuid():N}"[..12]),
            "Test Property", capacity: 4, condominiumId: null, address, now);
        property.Activate(now);

        propertyDbContext.Properties.Add(property);
        await propertyDbContext.SaveChangesAsync();
        await transaction.CommitAsync();

        // Housekeeping's own property-eligibility projection must exist for Reservations to accept a new booking.
        await using var connection = new NpgsqlConnection(_fixture.MigratorConnectionString);
        await connection.OpenAsync();
        await using var projectionTransaction = await connection.BeginTransactionAsync();
        await using (var setCommand = connection.CreateCommand())
        {
            setCommand.CommandText = $"SET LOCAL app.tenant_id = '{GlobalTenantId:D}'";
            await setCommand.ExecuteNonQueryAsync();
        }
        await using (var insertCommand = connection.CreateCommand())
        {
            insertCommand.CommandText =
                "INSERT INTO housekeeping.property_projection (tenant_id, property_id, is_active) VALUES (@tenantId, @propertyId, true)";
            insertCommand.Parameters.AddWithValue("tenantId", GlobalTenantId);
            insertCommand.Parameters.AddWithValue("propertyId", property.Id);
            await insertCommand.ExecuteNonQueryAsync();
        }
        await projectionTransaction.CommitAsync();

        return property.Id;
    }

    private async Task<Message?> WaitForInboundMessageAsync(Guid tenantId, string providerMessageId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var message = await ReadMessageByProviderMessageIdAsync(tenantId, providerMessageId);
            if (message is not null)
                return message;
            await Task.Delay(TimeSpan.FromMilliseconds(300));
        }
        return await ReadMessageByProviderMessageIdAsync(tenantId, providerMessageId);
    }

    private async Task<Message?> ReadMessageByProviderMessageIdAsync(Guid tenantId, string providerMessageId)
    {
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);
        await using var dbContext = CreateCommunicationDbContext(tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext.Database, tenantId);

        var message = await dbContext.Messages.AsNoTracking()
            .FirstOrDefaultAsync(m => m.TenantId == tenantId && m.ProviderMessageId == providerMessageId);

        await transaction.CommitAsync();
        return message;
    }

    private async Task<int> CountMessagesByProviderIdAsync(Guid tenantId, string providerMessageId)
    {
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);
        await using var dbContext = CreateCommunicationDbContext(tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext.Database, tenantId);

        var count = await dbContext.Messages.AsNoTracking()
            .CountAsync(m => m.TenantId == tenantId && m.ProviderMessageId == providerMessageId);

        await transaction.CommitAsync();
        return count;
    }

    private async Task<int> CountConversationsAsync(Guid tenantId, Guid reservationId)
    {
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);
        await using var dbContext = CreateCommunicationDbContext(tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext.Database, tenantId);

        var count = await dbContext.Conversations.AsNoTracking()
            .CountAsync(c => c.TenantId == tenantId && c.ReservationId == reservationId);

        await transaction.CommitAsync();
        return count;
    }

    private static async Task SetTenantAsync(Microsoft.EntityFrameworkCore.Infrastructure.DatabaseFacade database, Guid tenantId) =>
        await database.ExecuteSqlInterpolatedAsync($"SELECT set_config('app.tenant_id', {tenantId.ToString()}, true)");

    private CommunicationDbContext CreateCommunicationDbContext(ITenantContext tenantContext)
    {
        var options = new DbContextOptionsBuilder<CommunicationDbContext>()
            .UseNpgsql(_fixture.MigratorConnectionString, npgsqlOptions => npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "communication"))
            .Options;
        return new CommunicationDbContext(options, tenantContext);
    }
}
