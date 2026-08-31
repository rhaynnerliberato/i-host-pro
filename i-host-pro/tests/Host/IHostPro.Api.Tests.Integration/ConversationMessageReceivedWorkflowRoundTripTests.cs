using System.Diagnostics;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.Contexts.AIAgent.Domain;
using IHostPro.Contexts.AIAgent.Infrastructure.ModelProviders;
using IHostPro.Contexts.AIAgent.Infrastructure.Persistence;
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
/// Fase 11, Checkpoint 2 (AI Agent Foundation) — mandatory real transport E2E
/// gate (mandate items 38-42). A signed WhatsApp inbound-message webhook,
/// received by the real <c>IHostPro.Api</c> pipeline, flows through the
/// ALREADY-proven CP1 chain (real durable outbox → real RabbitMQ → real
/// <c>IHostPro.Worker</c> subprocess → <c>InboundGuestMessageProcessor</c> →
/// persisted <c>Conversation</c>/<c>Message</c> → <c>ConversationMessageReceived</c>
/// published) and CONTINUES into the new CP2 chain, in the SAME Worker
/// process: AI Agent's own keyed Wolverine consumer
/// (<c>ConversationMessageReceivedProcessor</c>) → resolve/create
/// <see cref="AgentSession"/> → ADR-030's real <c>IConversationHistoryReader</c>
/// against real Communication data → the real (deterministic, zero-network)
/// <see cref="FakeModelProvider"/> → a persisted <see cref="AgentInteraction"/>.
/// Never simulates Meta itself — only the HTTP request/signature Meta would
/// send. Mirrors <c>InboundGuestMessageWorkflowRoundTripTests</c>'s dual-process
/// infrastructure exactly (same Fixture shape, independent instance — this
/// checkpoint's own scenarios are additive, never modifying CP1's own gate).
///
/// Proves, per Fact: exactly one AgentSession/AgentInteraction for the
/// principal flow (item 38); exactly one AgentInteraction — and exactly one
/// FakeModelProvider execution — when the same MessageId is delivered twice
/// (item 39); the SAME Active AgentSession reused across two different
/// inbound messages in the same Conversation, two AgentInteractions,
/// chronological processing (item 40); a pre-existing sensitive/redacted
/// message in history never reaches the model in reconstructed form (item
/// 41); a controlled FakeModelProvider failure persists an
/// <see cref="AgentInteractionOutcome.Failure"/> AgentInteraction with no
/// crash loop and no duplicate session (item 42).
/// </summary>
public sealed class ConversationMessageReceivedWorkflowRoundTripTests : IClassFixture<ConversationMessageReceivedWorkflowRoundTripTests.Fixture>
{
    private const string PhoneNumberId = "e2e-aiagent-phone-number-id";
    private const string AppSecret = "e2e-aiagent-test-app-secret";
    private const string VerifyToken = "e2e-aiagent-test-verify-token";

    private readonly Fixture _fixture;

    public ConversationMessageReceivedWorkflowRoundTripTests(Fixture fixture) => _fixture = fixture;

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

            var listeningToInbound = await WaitForWorkerLogLineAsync(
                "Started message listening at rabbitmq://queue/communication.inbound-guest-message-trigger", TimeSpan.FromSeconds(45));
            var listeningToAiAgent = await WaitForWorkerLogLineAsync(
                "Started message listening at rabbitmq://queue/aiagent.conversation-message-trigger", TimeSpan.FromSeconds(45));
            if (!listeningToInbound || !listeningToAiAgent)
            {
                string snapshot;
                lock (_workerOutputLock) snapshot = _workerOutput.ToString();
                throw new InvalidOperationException(
                    $"Worker never reported listening to both required queues (inbound={listeningToInbound}, aiAgent={listeningToAiAgent}). Worker output:\n{snapshot}");
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
            ["OpenTelemetry__OtlpEndpoint"] = "http://127.0.0.1:14324",
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

    // Every scenario in this class shares one tenant/one WhatsAppTenantRoute
    // (seeded once by the Fixture) — each [Fact] uses its own phone number so
    // Reservation-resolution candidates never leak across scenarios.
    //
    // Internal (not private): Fixture.SeedTenantRouteAsync() below always
    // seeds the WhatsAppTenantRoute for THIS exact field (nested-class
    // access to the outer type's own static member) — any other test class
    // reusing this same Fixture type (e.g. AIAgentReadToolsWorkflowRoundTripTests,
    // Fase 11 Checkpoint 3) must send its inbound webhooks under this SAME
    // tenant id, never a freshly-generated one of its own, or the phone
    // number → tenant resolution silently never matches.
    internal static readonly Guid GlobalTenantId = Guid.NewGuid();

    [Fact]
    public async Task A_single_inbound_message_creates_one_AgentSession_and_one_successful_AgentInteraction()
    {
        const string guestPhone = "+5511911119999";
        var reservationId = await SeedConfirmedReservationAsync(guestPhone);

        var response = await SendInboundMessageAsync("wamid.AIAGENT-E2E-SINGLE", "5511911119999", "Olá, preciso de ajuda");
        response.EnsureSuccessStatusCode();

        var message = await WaitForInboundMessageAsync(GlobalTenantId, "wamid.AIAGENT-E2E-SINGLE", TimeSpan.FromSeconds(30));
        message.Should().NotBeNull("Worker output:\n" + _fixture.GetWorkerOutputSnapshot());

        var interaction = await WaitForInteractionAsync(GlobalTenantId, message!.Id, TimeSpan.FromSeconds(30));
        interaction.Should().NotBeNull("the AI Agent chain must persist an AgentInteraction within 30s. Worker output:\n" + _fixture.GetWorkerOutputSnapshot());
        interaction!.Outcome.Should().Be(AgentInteractionOutcome.Success);

        var sessionCount = await CountSessionsAsync(GlobalTenantId, message.ConversationId);
        sessionCount.Should().Be(1);

        var session = await ReadSessionAsync(GlobalTenantId, message.ConversationId);
        session!.ReservationId.Should().Be(reservationId);
        session.Status.Should().Be(AgentSessionStatus.Active);
    }

    [Fact]
    public async Task The_same_MessageId_delivered_twice_persists_exactly_one_AgentInteraction()
    {
        const string guestPhone = "+5511922229999";
        await SeedConfirmedReservationAsync(guestPhone);

        for (var i = 0; i < 2; i++)
        {
            var response = await SendInboundMessageAsync("wamid.AIAGENT-E2E-DUP", "5511922229999", "oi de novo");
            response.EnsureSuccessStatusCode();
        }

        var message = await WaitForInboundMessageAsync(GlobalTenantId, "wamid.AIAGENT-E2E-DUP", TimeSpan.FromSeconds(30));
        message.Should().NotBeNull("Worker output:\n" + _fixture.GetWorkerOutputSnapshot());

        var interaction = await WaitForInteractionAsync(GlobalTenantId, message!.Id, TimeSpan.FromSeconds(30));
        interaction.Should().NotBeNull("Worker output:\n" + _fixture.GetWorkerOutputSnapshot());

        await Task.Delay(TimeSpan.FromSeconds(5));

        (await CountInteractionsAsync(GlobalTenantId, message.Id)).Should().Be(1,
            "a redelivered ConversationMessageReceived must never produce a second AgentInteraction (FakeModelProvider called once)");
    }

    [Fact]
    public async Task Two_different_messages_in_the_same_Conversation_reuse_the_Active_session_and_produce_two_interactions()
    {
        const string guestPhone = "+5511933339999";
        await SeedConfirmedReservationAsync(guestPhone);

        var firstResponse = await SendInboundMessageAsync("wamid.AIAGENT-E2E-MULTI-1", "5511933339999", "primeira mensagem");
        firstResponse.EnsureSuccessStatusCode();
        var firstMessage = await WaitForInboundMessageAsync(GlobalTenantId, "wamid.AIAGENT-E2E-MULTI-1", TimeSpan.FromSeconds(30));
        firstMessage.Should().NotBeNull("Worker output:\n" + _fixture.GetWorkerOutputSnapshot());
        var firstInteraction = await WaitForInteractionAsync(GlobalTenantId, firstMessage!.Id, TimeSpan.FromSeconds(30));
        firstInteraction.Should().NotBeNull("Worker output:\n" + _fixture.GetWorkerOutputSnapshot());

        var secondResponse = await SendInboundMessageAsync("wamid.AIAGENT-E2E-MULTI-2", "5511933339999", "segunda mensagem");
        secondResponse.EnsureSuccessStatusCode();
        var secondMessage = await WaitForInboundMessageAsync(GlobalTenantId, "wamid.AIAGENT-E2E-MULTI-2", TimeSpan.FromSeconds(30));
        secondMessage.Should().NotBeNull("Worker output:\n" + _fixture.GetWorkerOutputSnapshot());
        var secondInteraction = await WaitForInteractionAsync(GlobalTenantId, secondMessage!.Id, TimeSpan.FromSeconds(30));
        secondInteraction.Should().NotBeNull("Worker output:\n" + _fixture.GetWorkerOutputSnapshot());

        firstMessage.ConversationId.Should().Be(secondMessage!.ConversationId, "both inbound messages must land in the same Conversation");
        (await CountSessionsAsync(GlobalTenantId, firstMessage.ConversationId)).Should().Be(1, "the SAME Active AgentSession must be reused, never a second one");
        secondInteraction!.AgentSessionId.Should().Be(firstInteraction!.AgentSessionId);
        secondInteraction.StartedAtUtc.Should().BeOnOrAfter(firstInteraction.StartedAtUtc, "processing must remain chronological");
    }

    [Fact]
    public async Task A_pre_existing_sensitive_marker_in_history_is_never_reconstructed_for_the_model()
    {
        const string guestPhone = "+5511944449999";
        var reservationId = await SeedConfirmedReservationAsync(guestPhone);
        var conversationId = await SeedSensitiveOutboundMessageAsync(reservationId);

        var response = await SendInboundMessageAsync("wamid.AIAGENT-E2E-SENSITIVE", "5511944449999", "oi, sobre o acesso?");
        response.EnsureSuccessStatusCode();

        var message = await WaitForInboundMessageAsync(GlobalTenantId, "wamid.AIAGENT-E2E-SENSITIVE", TimeSpan.FromSeconds(30));
        message.Should().NotBeNull("Worker output:\n" + _fixture.GetWorkerOutputSnapshot());
        message!.ConversationId.Should().Be(conversationId, "the inbound message must land in the SAME Conversation as the pre-seeded sensitive one");

        var interaction = await WaitForInteractionAsync(GlobalTenantId, message.Id, TimeSpan.FromSeconds(30));
        interaction.Should().NotBeNull("Worker output:\n" + _fixture.GetWorkerOutputSnapshot());
        interaction!.Outcome.Should().Be(AgentInteractionOutcome.Success,
            "the model call must succeed normally — the sensitive marker is inert text to FakeModelProvider, never a credential it could act on");

        // AgentInteraction never persists response/prompt text (governance
        // resolution item 10) — there is no field to leak the credential
        // sentinel into. This assertion documents that guarantee structurally
        // rather than by inspecting a string (never printing the sentinel
        // itself anywhere, mandate item 41's own instruction).
        typeof(AgentInteraction).GetProperty("ResponseText").Should().BeNull();
    }

    [Fact]
    public async Task A_controlled_FakeModelProvider_failure_persists_a_Failure_interaction_with_no_crash_loop()
    {
        const string guestPhone = "+5511955559999";
        await SeedConfirmedReservationAsync(guestPhone);

        var response = await SendInboundMessageAsync(
            "wamid.AIAGENT-E2E-FAILURE", "5511955559999", $"mensagem de teste {FakeModelProvider.FailureTriggerMarker}");
        response.EnsureSuccessStatusCode();

        var message = await WaitForInboundMessageAsync(GlobalTenantId, "wamid.AIAGENT-E2E-FAILURE", TimeSpan.FromSeconds(30));
        message.Should().NotBeNull("Worker output:\n" + _fixture.GetWorkerOutputSnapshot());

        var interaction = await WaitForInteractionAsync(GlobalTenantId, message!.Id, TimeSpan.FromSeconds(30));
        interaction.Should().NotBeNull("Worker output:\n" + _fixture.GetWorkerOutputSnapshot());
        interaction!.Outcome.Should().Be(AgentInteractionOutcome.Failure);

        // No duplicate session, no crash loop: exactly one session, exactly
        // one interaction, even after the controlled failure settles.
        await Task.Delay(TimeSpan.FromSeconds(5));
        (await CountSessionsAsync(GlobalTenantId, message.ConversationId)).Should().Be(1);
        (await CountInteractionsAsync(GlobalTenantId, message.Id)).Should().Be(1);
    }

    // ---- Helpers ----------------------------------------------------------

    private async Task<HttpResponseMessage> SendInboundMessageAsync(string providerMessageId, string from, string? text)
    {
        var textPart = text is null ? "" : ",\"text\":{\"body\":\"" + text + "\"}";
        var body = "{\"entry\":[{\"changes\":[{\"value\":{" +
            "\"metadata\":{\"phone_number_id\":\"" + PhoneNumberId + "\"}," +
            "\"messages\":[{\"id\":\"" + providerMessageId + "\",\"from\":\"" + from + "\",\"type\":\"text\"," +
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

    /// <summary>
    /// Seeds a Conversation + one OUTBOUND Message whose persisted content is
    /// already the fixed <c>"[SENSITIVE CONTENT REDACTED]"</c> marker
    /// (mirrors what <c>GuestAccessDeliveryProcessor</c> actually persists,
    /// ADR-028) — bypasses the full credential-delivery event chain
    /// deliberately: this test's own concern is the AI Agent history
    /// boundary, not re-proving Guest Access delivery (already covered by
    /// its own dedicated tests).
    /// </summary>
    private async Task<Guid> SeedSensitiveOutboundMessageAsync(Guid reservationId)
    {
        const string sensitiveMarker = "[SENSITIVE CONTENT REDACTED]";
        var conversationId = Guid.NewGuid();
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(GlobalTenantId);

        await using var dbContext = CreateCommunicationDbContext(tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext.Database, GlobalTenantId);

        var conversation = Conversation.Create(conversationId, GlobalTenantId, reservationId, "WhatsApp", DateTimeOffset.UtcNow);
        dbContext.Conversations.Add(conversation);

        var message = Message.Create(
            Guid.NewGuid(), GlobalTenantId, conversationId, reservationId, "WhatsApp", "GUEST_ACCESS_CREDENTIAL",
            null, sensitiveMarker, $"idem-{Guid.NewGuid():N}", DateTimeOffset.UtcNow);
        message.MarkQueued();
        message.MarkSending();
        message.MarkSent(DateTimeOffset.UtcNow);
        dbContext.Messages.Add(message);

        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();

        return conversationId;
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

    private async Task<AgentInteraction?> WaitForInteractionAsync(Guid tenantId, Guid inboundMessageId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var interaction = await ReadInteractionAsync(tenantId, inboundMessageId);
            if (interaction is not null)
                return interaction;
            await Task.Delay(TimeSpan.FromMilliseconds(300));
        }
        return await ReadInteractionAsync(tenantId, inboundMessageId);
    }

    private async Task<AgentInteraction?> ReadInteractionAsync(Guid tenantId, Guid inboundMessageId)
    {
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);
        await using var dbContext = CreateAIAgentDbContext(tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext.Database, tenantId);

        var interaction = await dbContext.AgentInteractions.AsNoTracking()
            .FirstOrDefaultAsync(i => i.TenantId == tenantId && i.InboundMessageId == inboundMessageId);

        await transaction.CommitAsync();
        return interaction;
    }

    private async Task<int> CountInteractionsAsync(Guid tenantId, Guid inboundMessageId)
    {
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);
        await using var dbContext = CreateAIAgentDbContext(tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext.Database, tenantId);

        var count = await dbContext.AgentInteractions.AsNoTracking()
            .CountAsync(i => i.TenantId == tenantId && i.InboundMessageId == inboundMessageId);

        await transaction.CommitAsync();
        return count;
    }

    private async Task<int> CountSessionsAsync(Guid tenantId, Guid conversationId)
    {
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);
        await using var dbContext = CreateAIAgentDbContext(tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext.Database, tenantId);

        var count = await dbContext.AgentSessions.AsNoTracking()
            .CountAsync(s => s.TenantId == tenantId && s.ConversationId == conversationId);

        await transaction.CommitAsync();
        return count;
    }

    private async Task<AgentSession?> ReadSessionAsync(Guid tenantId, Guid conversationId)
    {
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);
        await using var dbContext = CreateAIAgentDbContext(tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext.Database, tenantId);

        var session = await dbContext.AgentSessions.AsNoTracking()
            .FirstOrDefaultAsync(s => s.TenantId == tenantId && s.ConversationId == conversationId);

        await transaction.CommitAsync();
        return session;
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

    private AIAgentDbContext CreateAIAgentDbContext(ITenantContext tenantContext)
    {
        var options = new DbContextOptionsBuilder<AIAgentDbContext>()
            .UseNpgsql(_fixture.MigratorConnectionString, npgsqlOptions => npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "ai_agent"))
            .Options;
        return new AIAgentDbContext(options, tenantContext);
    }
}
