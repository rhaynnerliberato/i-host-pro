using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.Contexts.AIAgent.Domain;
using IHostPro.Contexts.AIAgent.Infrastructure.Persistence;
using IHostPro.Contexts.Communication.Domain;
using IHostPro.Contexts.Communication.Infrastructure.Persistence;
using IHostPro.Contexts.ExternalIntegrations.Domain;
using IHostPro.Contexts.ExternalIntegrations.Infrastructure.Persistence;
using IHostPro.Contexts.Reservations.Application;
using IHostPro.Contexts.Reservations.Application.Reservations;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;

namespace IHostPro.Api.Tests.Integration;

/// <summary>
/// Fase 11, Checkpoint 7 (Anthropic Claude Real Proof) — the mandatory REAL
/// full-cycle E2E gate, mirroring <c>AIAgentReadToolsWorkflowRoundTripTests</c>/
/// <c>AIAgentHumanHandoffWorkflowRoundTripTests</c>' own infrastructure and
/// assertion shape exactly, but with <c>AIAgent:ModelProvider=Anthropic</c>
/// instead of the default Fake — a signed WhatsApp inbound webhook flows
/// through the real Api → real outbox → real RabbitMQ → a real Worker
/// subprocess → AI Agent's own consumer → a REAL <c>AnthropicModelProvider</c>
/// making REAL HTTP calls to the real Anthropic Messages API (never a mock,
/// never a fake HTTP handler).
///
/// Dev-gated (own <see cref="Fixture"/>, own containers — never shares
/// <c>ConversationMessageReceivedWorkflowRoundTripTests.Fixture</c>, never
/// contaminates the deterministic 87-test regression): if no local Anthropic
/// API key is configured (checked via the SAME <c>IConfiguration</c> source
/// <c>AnthropicRealProofTests</c> uses — User Secrets on
/// <c>IHostPro.Worker</c>'s own store, or an environment variable), every
/// <see cref="Fixture.InitializeAsync"/> call returns immediately without
/// starting any container/process, and every <c>[Fact]</c> passes trivially.
/// A missing local credential is never a code defect and must never block
/// publication (CP7 mandate item 13/75).
///
/// Exactly two scenarios, deliberately minimizing real (paid) Anthropic
/// network calls: the Read Tool full cycle (Call#1 tool selection → real
/// <c>GetReservationSummary</c> execution → Call#2 with the tool result →
/// real delivered response — 2 real calls) and the Human Handoff full cycle
/// (real classification → escalation → notification, immediately followed by
/// a second inbound message on the now-Escalated session, proving ZERO
/// further real Anthropic calls happen post-escalation — 1 real call total
/// for the whole scenario). RealWriteToolProof stays <c>false</c>: neither
/// scenario ever offers or exercises a Write Tool.
///
/// The handoff acknowledgment text is a FIXED, backend-composed constant
/// (see <c>ConversationMessageReceivedProcessor.HandoffNotifiedAckContent</c>/
/// <c>HandoffRequestedOnlyAckContent</c>) — never model-generated free text —
/// so asserting its exact contents is provider-agnostic and exactly as valid
/// here as in the deterministic Fake-provider test it mirrors.
/// </summary>
public sealed class AnthropicRealAgentWorkflowRoundTripTests : IClassFixture<AnthropicRealAgentWorkflowRoundTripTests.Fixture>
{
    private const string PhoneNumberId = "e2e-anthropic-real-phone-number-id";
    private const string AppSecret = "e2e-anthropic-real-test-app-secret";

    private readonly Fixture _fixture;

    public AnthropicRealAgentWorkflowRoundTripTests(Fixture fixture) => _fixture = fixture;

    private static readonly Guid GlobalTenantId = Guid.NewGuid();

    public sealed class Fixture : IAsyncLifetime
    {
        private const string AppRolePassword = "test_app_password";
        private const string MigratorRolePassword = "test_migrator_password";
        private const string Issuer = "https://identity.ihostpro.test";
        private const string Audience = "ihostpro-api-test";
        private const string WorkerUserSecretsId = "dotnet-IHostPro.Worker-cc769433-0535-453a-bbdf-17f44d398b0c";
        private const string AnthropicApiKeyConfigurationKey = "AIAgent:Anthropic:Secrets:ApiKey";

        private PostgreSqlContainer _postgresContainer = null!;
        private RabbitMqContainer _rabbitMqContainer = null!;
        private Process? _workerProcess;
        private WebApplicationFactory<Program>? _apiFactory;
        private readonly Dictionary<string, string?> _envValues = [];

        /// <summary>
        /// <see langword="false"/> when no local Anthropic API key is
        /// configured — every <c>[Fact]</c> checks this first and returns
        /// immediately without touching <see cref="ApiClient"/>/<see cref="ApiServices"/>
        /// (never started in that case). Checked via the exact same
        /// <c>IConfiguration</c> source as <c>AnthropicRealProofTests</c> —
        /// never <c>dotnet user-secrets list</c>, never printed.
        /// </summary>
        public bool CredentialAvailable { get; private set; }

        public string MigratorConnectionString { get; private set; } = null!;
        public string AppConnectionString { get; private set; } = null!;
        public HttpClient ApiClient { get; private set; } = null!;
        public IServiceProvider ApiServices => _apiFactory!.Services;

        public async Task InitializeAsync()
        {
            var apiKey = new ConfigurationBuilder()
                .AddUserSecrets(WorkerUserSecretsId)
                .AddEnvironmentVariables()
                .Build()[AnthropicApiKeyConfigurationKey];

            if (string.IsNullOrWhiteSpace(apiKey))
            {
                CredentialAvailable = false;
                return;
            }
            CredentialAvailable = true;

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
            if (!CredentialAvailable) return;

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
            ["OpenTelemetry__OtlpEndpoint"] = "http://127.0.0.1:14323",

            // Fase 11, Checkpoint 7 — the ONE difference from every other
            // RoundTrip fixture in this directory: selects the real
            // Anthropic REST provider instead of the default Fake. The API
            // key itself is deliberately NEVER passed here — with
            // DOTNET_ENVIRONMENT=Development set above and IHostPro.Worker's
            // own <UserSecretsId> baked into its assembly, .NET's Generic
            // Host automatically loads the SAME local User Secrets store
            // this Fixture already confirmed has the key (InitializeAsync's
            // own CredentialAvailable check above) — this process never
            // reads, holds, or forwards the value itself.
            ["AIAgent__ModelProvider"] = "Anthropic",
        };

        private Dictionary<string, string?> BuildApiEnvironment(string signingKeyPem)
        {
            var values = new Dictionary<string, string?>();
            foreach (var (key, value) in BuildWorkerEnvironment(signingKeyPem))
                values[key] = value;
            values["ExternalIntegrations__WhatsApp__Webhook__AppSecret"] = AppSecret;
            values["ExternalIntegrations__WhatsApp__Webhook__VerifyToken"] = "e2e-anthropic-real-verify-token";
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

    [Fact]
    public async Task RealAnthropic_ReadTool_full_cycle_executes_GetReservationSummary_and_delivers_a_real_outbound_response()
    {
        if (!_fixture.CredentialAvailable) return;

        const string guestPhone = "+5511850000001";
        var reservationId = await SeedConfirmedReservationAsync(guestPhone, DateTimeOffset.UtcNow.AddDays(30), DateTimeOffset.UtcNow.AddDays(33));

        var response = await SendInboundMessageAsync("wamid.ANTHROPIC-E2E-READTOOL", "5511850000001", "Qual é a data do meu check-in?");
        response.EnsureSuccessStatusCode();

        var message = await WaitForInboundMessageAsync("wamid.ANTHROPIC-E2E-READTOOL");
        message.Should().NotBeNull(WorkerSnapshot());

        var interaction = await WaitForInteractionAsync(message!.Id);
        interaction.Should().NotBeNull(WorkerSnapshot());
        interaction!.Outcome.Should().Be(AgentInteractionOutcome.Success, WorkerSnapshot());
        interaction.ModelProvider.Should().Be("Anthropic");
        interaction.ModelName.Should().Be("claude-sonnet-4-6");
        interaction.InputTokens.Should().BeGreaterThan(0);
        interaction.OutputTokens.Should().BeGreaterThan(0);
        interaction.EstimatedCostUsd.Should().NotBeNull().And.BeGreaterThan(0);
        interaction.CostPricingReference.Should().NotBeNullOrWhiteSpace();

        var toolExecutions = await ReadToolExecutionsAsync(interaction.Id);
        toolExecutions.Should().ContainSingle(
            "the real Anthropic model must choose exactly one Read Tool for this question — never zero, never more (Call#1/Call#2 discipline) — " + WorkerSnapshot());
        toolExecutions[0].ToolName.Should().Be("GetReservationSummary");
        toolExecutions[0].Outcome.Should().Be(AgentToolExecutionOutcome.Success, WorkerSnapshot());
        toolExecutions[0].AgentInteractionId.Should().Be(interaction.Id);

        interaction.OutboundMessageId.Should().NotBeNull(WorkerSnapshot());
        var outboundMessage = await ReadMessageByIdAsync(interaction.OutboundMessageId!.Value);
        outboundMessage.Should().NotBeNull();
        outboundMessage!.Direction.Should().Be(MessageDirection.Outbound);
        outboundMessage.ConversationId.Should().Be(message.ConversationId);
        outboundMessage.ReservationId.Should().Be(reservationId);
        outboundMessage.RenderedContent.Should().NotBeNullOrWhiteSpace("the real model's own final answer (Call#2), delivered as a real outbound Message");

        (await CountOutboundMessagesAsync(message.ConversationId)).Should().Be(1);
    }

    [Fact]
    public async Task RealAnthropic_HumanHandoff_full_cycle_escalates_the_session_and_a_follow_up_message_never_reaches_the_model_again()
    {
        if (!_fixture.CredentialAvailable) return;

        const string guestPhone = "+5511850000002";
        await SeedConfirmedReservationAsync(guestPhone, DateTimeOffset.UtcNow.AddDays(30), DateTimeOffset.UtcNow.AddDays(33));
        await SeedAdministratorContactAsync("+5511999990099");

        var firstResponse = await SendInboundMessageAsync("wamid.ANTHROPIC-E2E-HANDOFF-1", "5511850000002", "Quero falar com uma pessoa, por favor.");
        firstResponse.EnsureSuccessStatusCode();

        var firstMessage = await WaitForInboundMessageAsync("wamid.ANTHROPIC-E2E-HANDOFF-1");
        firstMessage.Should().NotBeNull(WorkerSnapshot());
        var firstInteraction = await WaitForInteractionAsync(firstMessage!.Id);
        firstInteraction.Should().NotBeNull(WorkerSnapshot());
        firstInteraction!.Outcome.Should().Be(AgentInteractionOutcome.Success, WorkerSnapshot());
        firstInteraction.ModelProvider.Should().Be("Anthropic");
        firstInteraction.Intent.Should().Be(
            "human_handoff_requested", "the real Anthropic model must classify this explicit request via the respond_to_guest control tool's own intent field");

        (await ReadToolExecutionsAsync(firstInteraction.Id)).Should().BeEmpty(
            "a restricted intent preempts every business Tool — RealWriteToolProof stays false for this scenario");

        var session = await ReadSessionByIdAsync(firstInteraction.AgentSessionId);
        session.Should().NotBeNull();
        session!.Status.Should().Be(AgentSessionStatus.Escalated, WorkerSnapshot());

        var handoff = await WaitForHandoffAsync(firstInteraction.AgentSessionId, h => h.Status == AgentHumanHandoffStatus.Notified);
        handoff.Should().NotBeNull("a real, seeded AdministratorNotificationContact must let notification genuinely succeed — " + WorkerSnapshot());
        handoff!.ReasonCode.Should().Be(AgentHumanHandoffReasonCode.ExplicitHumanRequest);
        handoff.NotifiedAtUtc.Should().NotBeNull();

        var firstOutboundId = await WaitForOutboundMessageIdAsync(firstMessage.Id);
        firstOutboundId.Should().NotBeNull(WorkerSnapshot());
        var firstOutboundMessage = await ReadMessageByIdAsync(firstOutboundId!.Value);
        firstOutboundMessage!.RenderedContent.Should().Contain("encaminhada", "the deterministic, backend-composed ack must reflect the real, genuine notification success");

        // ---- Suspension proof (PostEscalationAnthropicCalls=0): a
        // follow-up message on the now-Escalated session must never reach
        // the real Anthropic model again. Proven by the SAME structural
        // signal the deterministic Fake-provider test uses — Intent stays
        // null because the suspended-session guard intercepts BEFORE
        // IModelProvider.GenerateAsync is ever called, entirely independent
        // of which provider is configured — plus zero additional Tool
        // executions and an unchanged handoff count.
        var secondResponse = await SendInboundMessageAsync(
            "wamid.ANTHROPIC-E2E-HANDOFF-2", "5511850000002", "Por favor, pode me ajudar com o early check-in?");
        secondResponse.EnsureSuccessStatusCode();
        var secondMessage = await WaitForInboundMessageAsync("wamid.ANTHROPIC-E2E-HANDOFF-2");
        secondMessage.Should().NotBeNull(WorkerSnapshot());
        var secondInteraction = await WaitForInteractionAsync(secondMessage!.Id);
        secondInteraction.Should().NotBeNull(WorkerSnapshot());

        secondInteraction!.Outcome.Should().Be(AgentInteractionOutcome.Success, WorkerSnapshot());
        secondInteraction.AgentSessionId.Should().Be(firstInteraction.AgentSessionId, "the SAME escalated session must be reused, never a new one");
        secondInteraction.Intent.Should().BeNull("PostEscalationAnthropicCalls=0 — the suspended-session path never calls the real model, so there is no intent to classify");

        (await ReadToolExecutionsAsync(secondInteraction.Id)).Should().BeEmpty("zero real model/Tool calls on an already-escalated session — " + WorkerSnapshot());
        (await CountHandoffsAsync(firstInteraction.AgentSessionId)).Should().Be(1, "a duplicate trigger while already escalated must never create a second handoff");

        var secondOutboundId = await WaitForOutboundMessageIdAsync(secondMessage.Id);
        secondOutboundId.Should().NotBeNull("the suspended-session path still delivers a deterministic ack even with zero real model calls — " + WorkerSnapshot());
    }

    // ---- Helpers ----------------------------------------------------------

    private string WorkerSnapshot() => "Worker output:\n" + _fixture.GetWorkerOutputSnapshot();

    private async Task<HttpResponseMessage> SendInboundMessageAsync(string providerMessageId, string from, string? text)
    {
        var textPart = text is null ? "" : ",\"text\":{\"body\":\"" + text.Replace("\"", "\\\"") + "\"}";
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

    private async Task<Message?> WaitForInboundMessageAsync(string providerMessageId)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(45);
        while (DateTime.UtcNow < deadline)
        {
            var message = await ReadMessageByProviderMessageIdAsync(providerMessageId);
            if (message is not null)
                return message;
            await Task.Delay(TimeSpan.FromMilliseconds(300));
        }
        return await ReadMessageByProviderMessageIdAsync(providerMessageId);
    }

    private async Task<Message?> ReadMessageByProviderMessageIdAsync(string providerMessageId)
    {
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(GlobalTenantId);
        await using var dbContext = CreateCommunicationDbContext(tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext.Database, GlobalTenantId);

        var message = await dbContext.Messages.AsNoTracking()
            .FirstOrDefaultAsync(m => m.TenantId == GlobalTenantId && m.ProviderMessageId == providerMessageId);

        await transaction.CommitAsync();
        return message;
    }

    private async Task<Message?> ReadMessageByIdAsync(Guid messageId)
    {
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(GlobalTenantId);
        await using var dbContext = CreateCommunicationDbContext(tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext.Database, GlobalTenantId);

        var message = await dbContext.Messages.AsNoTracking().FirstOrDefaultAsync(m => m.Id == messageId);

        await transaction.CommitAsync();
        return message;
    }

    private async Task<int> CountOutboundMessagesAsync(Guid conversationId)
    {
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(GlobalTenantId);
        await using var dbContext = CreateCommunicationDbContext(tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext.Database, GlobalTenantId);

        var count = await dbContext.Messages.AsNoTracking()
            .CountAsync(m => m.TenantId == GlobalTenantId && m.ConversationId == conversationId && m.Direction == MessageDirection.Outbound);

        await transaction.CommitAsync();
        return count;
    }

    /// <summary>Waits for a COMPLETED interaction, not merely a persisted (possibly still InProgress) row — see the identical helper in AIAgentReadToolsWorkflowRoundTripTests for the CP3 polling-bug rationale. Timeout extended to 45s here — a real Anthropic round trip is slower than FakeModelProvider's zero-network response.</summary>
    private async Task<AgentInteraction?> WaitForInteractionAsync(Guid inboundMessageId)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(45);
        while (DateTime.UtcNow < deadline)
        {
            var interaction = await ReadInteractionAsync(inboundMessageId);
            if (interaction is not null && interaction.Outcome != AgentInteractionOutcome.InProgress)
                return interaction;
            await Task.Delay(TimeSpan.FromMilliseconds(300));
        }
        return await ReadInteractionAsync(inboundMessageId);
    }

    private async Task<AgentInteraction?> ReadInteractionAsync(Guid inboundMessageId)
    {
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(GlobalTenantId);
        await using var dbContext = CreateAIAgentDbContext(tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext.Database, GlobalTenantId);

        var interaction = await dbContext.AgentInteractions.AsNoTracking()
            .FirstOrDefaultAsync(i => i.TenantId == GlobalTenantId && i.InboundMessageId == inboundMessageId);

        await transaction.CommitAsync();
        return interaction;
    }

    private async Task<Guid?> WaitForOutboundMessageIdAsync(Guid inboundMessageId)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(45);
        while (DateTime.UtcNow < deadline)
        {
            var interaction = await ReadInteractionAsync(inboundMessageId);
            if (interaction?.OutboundMessageId is not null)
                return interaction.OutboundMessageId;
            await Task.Delay(TimeSpan.FromMilliseconds(300));
        }
        return (await ReadInteractionAsync(inboundMessageId))?.OutboundMessageId;
    }

    private async Task<List<AgentToolExecution>> ReadToolExecutionsAsync(Guid agentInteractionId)
    {
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(GlobalTenantId);
        await using var dbContext = CreateAIAgentDbContext(tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext.Database, GlobalTenantId);

        var executions = await dbContext.AgentToolExecutions.AsNoTracking()
            .Where(e => e.TenantId == GlobalTenantId && e.AgentInteractionId == agentInteractionId)
            .ToListAsync();

        await transaction.CommitAsync();
        return executions;
    }

    private async Task<AgentSession?> ReadSessionByIdAsync(Guid agentSessionId)
    {
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(GlobalTenantId);
        await using var dbContext = CreateAIAgentDbContext(tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext.Database, GlobalTenantId);

        var session = await dbContext.AgentSessions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == agentSessionId);

        await transaction.CommitAsync();
        return session;
    }

    private async Task<AgentHumanHandoff?> ReadHandoffByAgentSessionIdAsync(Guid agentSessionId)
    {
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(GlobalTenantId);
        await using var dbContext = CreateAIAgentDbContext(tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext.Database, GlobalTenantId);

        var handoff = await dbContext.AgentHumanHandoffs.AsNoTracking().FirstOrDefaultAsync(h => h.AgentSessionId == agentSessionId);

        await transaction.CommitAsync();
        return handoff;
    }

    private async Task<AgentHumanHandoff?> WaitForHandoffAsync(Guid agentSessionId, Func<AgentHumanHandoff, bool> predicate)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(45);
        while (DateTime.UtcNow < deadline)
        {
            var handoff = await ReadHandoffByAgentSessionIdAsync(agentSessionId);
            if (handoff is not null && predicate(handoff))
                return handoff;
            await Task.Delay(TimeSpan.FromMilliseconds(300));
        }
        return await ReadHandoffByAgentSessionIdAsync(agentSessionId);
    }

    private async Task<int> CountHandoffsAsync(Guid agentSessionId)
    {
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(GlobalTenantId);
        await using var dbContext = CreateAIAgentDbContext(tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext.Database, GlobalTenantId);

        var count = await dbContext.AgentHumanHandoffs.AsNoTracking().CountAsync(h => h.AgentSessionId == agentSessionId);

        await transaction.CommitAsync();
        return count;
    }

    private async Task SeedAdministratorContactAsync(string phone)
    {
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(GlobalTenantId);
        await using var dbContext = CreateCommunicationDbContext(tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext.Database, GlobalTenantId);

        var contact = AdministratorNotificationContact.Create(Guid.NewGuid(), GlobalTenantId, phone, DateTimeOffset.UtcNow);
        dbContext.AdministratorNotificationContacts.Add(contact);
        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();
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

    // ---- Domain seeding -----------------------------------------------------

    private async Task<Guid> SeedConfirmedReservationAsync(string guestPhone, DateTimeOffset checkInAt, DateTimeOffset checkOutAt)
    {
        var propertyId = await SeedActivePropertyAsync();

        using var scope = _fixture.ApiServices.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(GlobalTenantId);
        var dispatcher = scope.ServiceProvider.GetRequiredService<IReservationsRequestDispatcher>();

        var result = await dispatcher.Send(new CreateReservationCommand(
            GlobalTenantId, Guid.NewGuid(), propertyId, "Test Guest", guestPhone, checkInAt, checkOutAt, GuestCount: 2));
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
            Guid.NewGuid(), GlobalTenantId, IHostPro.Contexts.PropertyManagement.Domain.ValueObjects.PropertyCode.Create($"CP7-{Guid.NewGuid():N}"[..12]),
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
}
