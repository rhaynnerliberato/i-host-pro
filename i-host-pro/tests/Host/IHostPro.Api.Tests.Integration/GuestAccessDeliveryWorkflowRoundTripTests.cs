using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using FluentAssertions;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.Contexts.GuestOperations.Application;
using IHostPro.Contexts.Identity.Application;
using IHostPro.Contexts.Identity.Infrastructure.Authentication;
using IHostPro.Contexts.PropertyManagement.Api.Contracts;
using IHostPro.Contexts.PropertyManagement.Application;
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
/// Real end-to-end proof of Fase 10, Checkpoint 6.2 (Guest Access Secure
/// Delivery Corrective Implementation) — the mandatory acceptance gate.
/// Every scenario runs against a real Postgres instance, a real RabbitMQ
/// broker, a real unmodified <c>IHostPro.Worker.dll</c> subprocess, and the
/// real HTTP surface of <c>IHostPro.Api</c> — mirrors
/// <c>PixPaymentWorkflowRoundTripTests</c>' own infrastructure exactly.
///
/// Central security property under test: a sentinel credential value
/// travels from a Development secret (env var, mirrors User Secrets) all
/// the way to a real persisted <c>communication.messages</c> row for the
/// INSTRUCTIONS intent's rendering (via the connector, proven at the unit
/// level), but the sentinel NEVER appears in ANY persisted row this test
/// can query — not in <c>property_access_configurations</c> (only the
/// reference is stored there), not in <c>communication.messages.rendered_content</c>
/// for the credential intent (redacted). The connector actually receiving
/// the real value is already proven with full precision at
/// <c>Communication.Tests.Unit.GuestAccessDeliveryProcessorTests</c> — this
/// E2E test proves the real infrastructure wiring around it, not that same
/// fact a second time (the deterministic <c>FakeWhatsAppConnector</c>
/// deliberately never logs/exposes dispatch content externally, by design).
/// </summary>
public sealed class GuestAccessDeliveryWorkflowRoundTripTests : IClassFixture<GuestAccessDeliveryWorkflowRoundTripTests.Fixture>
{
    private readonly Fixture _fixture;

    public GuestAccessDeliveryWorkflowRoundTripTests(Fixture fixture) => _fixture = fixture;

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
                "communication.guest-access-delivery-trigger",
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

        /// <summary>
        /// The Development secret backend this checkpoint uses
        /// (<c>DevelopmentPropertyAccessCredentialProvider</c>) resolves via
        /// <c>IConfiguration</c> — environment variables in this subprocess,
        /// the exact same mechanism User Secrets would use locally. Key
        /// shape mirrors <c>IConfiguration</c>'s own env-var binding
        /// convention (<c>:</c> becomes <c>__</c>).
        /// </summary>
        private static string SecretEnvironmentVariableName(string reference) =>
            $"PropertyManagement__GuestAccess__Secrets__{reference}";

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
            ["OpenTelemetry__OtlpEndpoint"] = "http://127.0.0.1:14321",
            // The one real secret this test seeds — mirrors how an operator
            // would configure User Secrets locally. SentinelAccessCredential
            // is a deliberately obvious, fake value — never a real door code.
            [SecretEnvironmentVariableName(SentinelReference)] = SentinelAccessCredential,
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

    // The sentinel access credential value — deliberately obvious/fake, used
    // to prove it never leaks into any persisted row this test can query.
    private const string SentinelReference = "e2e-front-door-code";
    private const string SentinelAccessCredential = "SENTINEL-SECRET-4F91B2-NEVER-PERSISTED";
    private const string SomeInstructions = "Wi-Fi: guest-network / senha-wifi: convidado2026";
    private const string RedactedContentMarker = "[SENSITIVE CONTENT REDACTED]";

    [Fact]
    public async Task RequestGuestAccessDelivery_delivers_credential_and_instructions_with_zero_credential_leakage()
    {
        var tenantId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var checkInAt = now.AddDays(5);
        var checkOutAt = now.AddDays(8);
        const string guestPhone = "+5511999998888";

        var propertyId = await SeedActivePropertyAsync(tenantId, now);
        var adminToken = await GenerateAdminTokenAsync(tenantId);

        // ---- Configure guest access via the real HTTP endpoint ----
        var configureResponse = await PutJsonAsync(
            $"/api/v1/properties/{propertyId}/access-configuration", adminToken,
            new { AccessCredentialSecretReference = SentinelReference, AccessInstructions = SomeInstructions, IsActive = true });
        configureResponse.StatusCode.Should().Be(HttpStatusCode.OK, await SafeReadBodyAsync(configureResponse));

        await SeedTemplateAsync(tenantId, "GUEST_ACCESS_CREDENTIAL", "Ola {{GuestName}}, o codigo de acesso e: {{AccessCredential}}");
        await SeedTemplateAsync(tenantId, "GUEST_ACCESS_INSTRUCTIONS", "Ola {{GuestName}}, instrucoes: {{AccessInstructions}}");

        var reservationId = await SeedConfirmedReservationAsync(tenantId, propertyId, checkInAt, checkOutAt, guestPhone);
        await WaitForGuestStayOperationStatusAsync(tenantId, reservationId, "Active");

        // ---- Trigger delivery via the real HTTP endpoint ----
        var deliveryResponse = await PostJsonAsync(
            $"/api/v1/guest-operations/reservations/{reservationId}/access-delivery", adminToken, new { });
        deliveryResponse.StatusCode.Should().Be(HttpStatusCode.OK, await SafeReadBodyAsync(deliveryResponse));

        var credentialMessageCreated = await WaitUntilAsync(
            () => GetMessageAsync(tenantId, reservationId, "GUEST_ACCESS_CREDENTIAL"), m => m is not null, TimeSpan.FromSeconds(30));
        credentialMessageCreated.Should().BeTrue(
            "the real GuestAccessDeliveryRequested -> Communication chain must create the credential Message within 30s. " +
            "Worker output:\n" + _fixture.GetWorkerOutputSnapshot());

        var instructionsMessageCreated = await WaitUntilAsync(
            () => GetMessageAsync(tenantId, reservationId, "GUEST_ACCESS_INSTRUCTIONS"), m => m is not null, TimeSpan.FromSeconds(30));
        instructionsMessageCreated.Should().BeTrue("the instructions Message must also be created. Worker output:\n" + _fixture.GetWorkerOutputSnapshot());

        var credentialMessage = (await GetMessageAsync(tenantId, reservationId, "GUEST_ACCESS_CREDENTIAL"))!.Value;
        var instructionsMessage = (await GetMessageAsync(tenantId, reservationId, "GUEST_ACCESS_INSTRUCTIONS"))!.Value;

        // ---- CRITICAL: zero credential leakage across every real persisted row ----
        credentialMessage.Status.Should().Be("Sent", "FakeWhatsAppConnector always succeeds");
        credentialMessage.RenderedContent.Should().Be(RedactedContentMarker,
            "the real credential must never be persisted — only the fixed redaction marker");
        credentialMessage.RenderedContent.Should().NotContain(SentinelAccessCredential);
        credentialMessage.DestinationMasked.Should().EndWith("8888", "the recipient must be the GUEST phone");

        instructionsMessage.Status.Should().Be("Sent");
        instructionsMessage.RenderedContent.Should().Contain(SomeInstructions, "instructions are not a secret — persisted normally");
        instructionsMessage.RenderedContent.Should().NotContain(SentinelAccessCredential,
            "the credential and instructions intents are fully independent — the credential must not leak into the other message either");

        var storedConfiguration = await GetPropertyAccessConfigurationRowAsync(tenantId, propertyId);
        storedConfiguration.Should().NotBeNull();
        storedConfiguration!.Value.AccessCredentialSecretReference.Should().Be(SentinelReference,
            "Property Management's own table stores only the reference — never the resolved raw secret");
        storedConfiguration.Value.AccessCredentialSecretReference.Should().NotBe(SentinelAccessCredential);
    }

    [Fact]
    public async Task RequestGuestAccessDelivery_is_idempotent_per_intent_when_requested_twice()
    {
        var tenantId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var checkInAt = now.AddDays(5);
        var checkOutAt = now.AddDays(8);

        var propertyId = await SeedActivePropertyAsync(tenantId, now);
        var adminToken = await GenerateAdminTokenAsync(tenantId);

        await PutJsonAsync(
            $"/api/v1/properties/{propertyId}/access-configuration", adminToken,
            new { AccessCredentialSecretReference = SentinelReference, AccessInstructions = SomeInstructions, IsActive = true });
        await SeedTemplateAsync(tenantId, "GUEST_ACCESS_CREDENTIAL", "Codigo: {{AccessCredential}}");
        await SeedTemplateAsync(tenantId, "GUEST_ACCESS_INSTRUCTIONS", "Instrucoes: {{AccessInstructions}}");

        var reservationId = await SeedConfirmedReservationAsync(tenantId, propertyId, checkInAt, checkOutAt, "+5511988887777");
        await WaitForGuestStayOperationStatusAsync(tenantId, reservationId, "Active");

        for (var i = 0; i < 2; i++)
        {
            var response = await PostJsonAsync(
                $"/api/v1/guest-operations/reservations/{reservationId}/access-delivery", adminToken, new { });
            response.StatusCode.Should().Be(HttpStatusCode.OK, await SafeReadBodyAsync(response));
        }

        var credentialCreated = await WaitUntilAsync(
            () => GetMessageAsync(tenantId, reservationId, "GUEST_ACCESS_CREDENTIAL"), m => m is not null, TimeSpan.FromSeconds(30));
        credentialCreated.Should().BeTrue("Worker output:\n" + _fixture.GetWorkerOutputSnapshot());

        // Settle window so a wrongly-duplicated second send has time to land before asserting its absence.
        await Task.Delay(TimeSpan.FromSeconds(5));

        (await CountMessagesAsync(tenantId, reservationId, "GUEST_ACCESS_CREDENTIAL")).Should().Be(1,
            "a duplicate access-delivery request must never send the credential twice");
        (await CountMessagesAsync(tenantId, reservationId, "GUEST_ACCESS_INSTRUCTIONS")).Should().Be(1,
            "a duplicate access-delivery request must never send the instructions twice");
    }

    [Fact]
    public async Task RequestGuestAccessDelivery_without_MANAGE_returns_403_and_with_no_token_returns_401()
    {
        var tenantId = Guid.NewGuid();
        var reservationId = Guid.NewGuid();

        var noTokenResponse = await _fixture.ApiClient.PostAsync(
            $"/api/v1/guest-operations/reservations/{reservationId}/access-delivery", JsonContent.Create(new { }));
        noTokenResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        using var scope = _fixture.ApiServices.CreateScope();
        var generator = scope.ServiceProvider.GetRequiredService<IJwtTokenGenerator>();
        var request = new JwtAccessTokenRequest(UserId: Guid.NewGuid(), TenantId: tenantId, SessionId: Guid.NewGuid(), Roles: ["HOUSEKEEPER"]);
        var noPermissionToken = generator.GenerateAccessToken(request).Token;

        var forbiddenResponse = await PostJsonAsync(
            $"/api/v1/guest-operations/reservations/{reservationId}/access-delivery", noPermissionToken, new { });
        forbiddenResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);
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

    private async Task SeedTemplateAsync(Guid tenantId, string key, string content)
    {
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

    private async Task<HttpResponseMessage> PutJsonAsync(string route, string token, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, route) { Content = JsonContent.Create(body) };
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

    private Task<(string Status, string? DestinationMasked, string RenderedContent)?> GetMessageAsync(Guid tenantId, Guid reservationId, string templateKey) =>
        QueryScopedAsync(tenantId, async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT status, destination_masked, rendered_content FROM communication.messages
                WHERE tenant_id = @tenantId AND reservation_id = @reservationId AND template_key = @templateKey
                """;
            command.Parameters.AddWithValue("tenantId", tenantId);
            command.Parameters.AddWithValue("reservationId", reservationId);
            command.Parameters.AddWithValue("templateKey", templateKey);

            await using var reader = await command.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
                return ((string Status, string? DestinationMasked, string RenderedContent)?)null;

            return (reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetString(2));
        });

    private Task<int> CountMessagesAsync(Guid tenantId, Guid reservationId, string templateKey) =>
        QueryScopedAsync(tenantId, async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT COUNT(*) FROM communication.messages
                WHERE tenant_id = @tenantId AND reservation_id = @reservationId AND template_key = @templateKey
                """;
            command.Parameters.AddWithValue("tenantId", tenantId);
            command.Parameters.AddWithValue("reservationId", reservationId);
            command.Parameters.AddWithValue("templateKey", templateKey);
            return Convert.ToInt32(await command.ExecuteScalarAsync());
        });

    private Task<(string? AccessCredentialSecretReference, string? AccessInstructions)?> GetPropertyAccessConfigurationRowAsync(Guid tenantId, Guid propertyId) =>
        QueryScopedAsync(tenantId, async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT access_credential_secret_reference, access_instructions FROM property_management.property_access_configurations
                WHERE tenant_id = @tenantId AND property_id = @propertyId
                """;
            command.Parameters.AddWithValue("tenantId", tenantId);
            command.Parameters.AddWithValue("propertyId", propertyId);

            await using var reader = await command.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
                return ((string?, string?)?)null;

            return (reader.IsDBNull(0) ? null : reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1));
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
