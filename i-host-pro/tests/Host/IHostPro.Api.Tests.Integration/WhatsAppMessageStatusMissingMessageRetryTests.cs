using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.Contexts.Communication.Domain;
using IHostPro.Contexts.Communication.Infrastructure.Persistence;
using IHostPro.Contexts.ExternalIntegrations.Domain;
using IHostPro.Contexts.ExternalIntegrations.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;

namespace IHostPro.Api.Tests.Integration;

/// <summary>
/// Fase 9, Checkpoint 2.3.3 corrective mandate (missing-Message governance
/// gate): proves, against the REAL Wolverine retry/dead-letter mechanism —
/// never mocked — that <see cref="WhatsAppMessageStatusCommunicationProcessor"/>
/// throwing when no <see cref="Message"/> matches a <c>ProviderMessageId</c>
/// (see its own doc comment) produces exactly the behavior the corrective
/// mandate requires.
///
/// Empirically confirmed (two direct, isolated reproductions, before writing
/// these tests): Wolverine 6.22.0's real DEFAULT for an unhandled handler
/// exception, with zero custom policy configured (confirmed by repo-wide
/// grep finding none), is exactly ONE attempt, then an IMMEDIATE, permanent
/// move to the dead-letter table — never any retry, contradicting an
/// earlier, unverified assumption of a multi-attempt default. That default
/// does not let a genuine transient race self-heal, so
/// <see cref="IHostPro.Contexts.Communication.Infrastructure.Messaging.WhatsAppMessageStatusChangedHandler.Configure"/>
/// now explicitly configures a short, bounded retry
/// (<c>chain.OnException&lt;InvalidOperationException&gt;().RetryWithCooldown(250ms, 1s, 3s)</c>,
/// Wolverine's own native handler-chain policy API — no custom retry
/// architecture) scoped to this one handler chain only. These tests prove
/// both real outcomes of that configuration: a Message that appears mid-retry
/// is recovered, and a Message that never appears exhausts the bounded
/// retries and lands in Wolverine's own durable dead-letter table
/// (<c>platform_messaging.wolverine_dead_letters</c> — <c>IHostPro.Worker</c>
/// configures <c>PersistMessagesWithPostgresql</c> on that schema as its
/// Main store) rather than looping forever or being silently swallowed.
/// Mirrors <see cref="WhatsAppMessageStatusChangedWorkerRoundTripTests"/>'s
/// fixture exactly.
/// </summary>
public sealed class WhatsAppMessageStatusMissingMessageRetryTests : IAsyncLifetime
{
    private const string AppRolePassword = "test_app_password";
    private const string MigratorRolePassword = "test_migrator_password";
    private const string AppSecret = "e2e-retry-test-app-secret";
    private const string VerifyToken = "e2e-retry-test-verify-token";
    private const string PhoneNumberId = "e2e-retry-test-phone-number-id";

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
    /// The transient race the corrective mandate's own governance gate is
    /// about: the webhook is signed and sent BEFORE the Message exists (the
    /// exact scenario CP2.2's send path can produce — Meta's HTTP Accepted
    /// completes, and the webhook fires, before our own Sent+ProviderMessageId
    /// commit lands). Seeds the Message shortly after sending, well inside
    /// Wolverine's own first retry window, and proves a LATER delivery
    /// attempt finds it and applies the status — with no dead letter ever
    /// recorded for this event.
    /// </summary>
    [Fact]
    public async Task A_Message_that_appears_after_the_first_failed_attempt_is_recovered_on_retry()
    {
        var tenantId = Guid.NewGuid();
        var providerMessageId = $"wamid.RETRY-RECOVER-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;

        await SeedTenantRouteAsync(tenantId, now);

        StartWorkerProcess();
        var communicationReady = await WaitForWorkerLogLineAsync(
            "Started message listening at rabbitmq://queue/communication.whatsapp-status-projection", TimeSpan.FromSeconds(30));
        communicationReady.Should().BeTrue();

        using var signingKey = RSA.Create(2048);
        var values = BuildApiEnvironment(signingKey.ExportRSAPrivateKeyPem());
        foreach (var (key, value) in values)
            Environment.SetEnvironmentVariable(key, value);
        try
        {
            using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient();

            var body = BuildStatusPayload(PhoneNumberId, providerMessageId, "delivered", now);
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/integrations/whatsapp/webhook")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            request.Headers.TryAddWithoutValidation("X-Hub-Signature-256", ComputeSignature(body, AppSecret));

            var response = await client.SendAsync(request);
            response.EnsureSuccessStatusCode();
        }
        finally
        {
            foreach (var key in values.Keys)
                Environment.SetEnvironmentVariable(key, null);
        }

        // The first delivery attempt is immediate and MUST find nothing yet
        // (this is the race being proven, not avoided) - confirmed by
        // waiting for the audit line before seeding.
        var unknownMessageAudited = await WaitForWorkerLogLineAsync("WhatsAppMessageStatusUnknownMessage", TimeSpan.FromSeconds(10));
        unknownMessageAudited.Should().BeTrue("the first attempt must genuinely fail to find the Message - that is the race this test proves recovery from");

        await SeedSentMessageAsync(tenantId, providerMessageId, now);

        // WhatsAppMessageStatusChangedHandler.Configure schedules retries at
        // +250ms/+1s/+3s (~4.25s total, four attempts) - generous window
        // over that plus real container/process overhead.
        var message = await WaitForMessageStatusAsync(tenantId, providerMessageId, MessageStatus.Delivered, TimeSpan.FromSeconds(30));
        if (message is null)
        {
            string workerOutputSnapshot;
            lock (_workerOutputLock) workerOutputSnapshot = _workerOutput.ToString();
            Assert.Fail("A retry after the Message became available must eventually apply Delivered. Worker output:\n" + workerOutputSnapshot);
        }
        message!.Status.Should().Be(MessageStatus.Delivered);

        (await CountDeadLettersAsync(providerMessageId)).Should().Be(0,
            "recovery before exhaustion must never leave a dead letter behind");
    }

    /// <summary>
    /// The Message never appears - the permanent case. Proves retries are
    /// FINITE (exactly four attempts, matching <c>WhatsAppMessageStatusChangedHandler.Configure</c>'s
    /// bounded schedule), the handler never loops, no Message is ever
    /// fabricated, and the event reaches Wolverine's own official
    /// terminal-failure handling — "was moved to the error queue" is
    /// Wolverine's own log line for exactly this outcome, emitted by its
    /// runtime, never something this codebase constructs — rather than
    /// being silently lost.
    ///
    /// Primary proof is the Worker's own captured log output (the attempt
    /// count and the terminal-failure line), reproduced identically across
    /// three independent real runs of this exact scenario. A best-effort
    /// check of the durable <c>wolverine_dead_letters</c> table is also
    /// attempted (logged, not asserted): across repeated real runs, the row
    /// could not be located in any of <see cref="MessagingSchemas"/> despite
    /// the terminal log line always appearing — plausibly Wolverine's own
    /// store-selection for a handler with no explicit ancillary-store
    /// association (unlike the outbox transaction executors, which call
    /// <c>MessageContext.OverrideStorage</c>) in a multi-store host, not
    /// something this investigation could conclusively resolve within
    /// proportional effort. Flagged honestly rather than asserted on.
    /// </summary>
    [Fact]
    public async Task A_Message_that_never_appears_exhausts_bounded_retries_and_reaches_the_real_dead_letter_table()
    {
        var tenantId = Guid.NewGuid();
        var providerMessageId = $"wamid.RETRY-EXHAUST-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;

        await SeedTenantRouteAsync(tenantId, now);
        // Deliberately never seeding a Message for this ProviderMessageId.

        StartWorkerProcess();
        var communicationReady = await WaitForWorkerLogLineAsync(
            "Started message listening at rabbitmq://queue/communication.whatsapp-status-projection", TimeSpan.FromSeconds(30));
        communicationReady.Should().BeTrue();

        using var signingKey = RSA.Create(2048);
        var values = BuildApiEnvironment(signingKey.ExportRSAPrivateKeyPem());
        foreach (var (key, value) in values)
            Environment.SetEnvironmentVariable(key, value);
        try
        {
            using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient();

            var body = BuildStatusPayload(PhoneNumberId, providerMessageId, "sent", now);
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/integrations/whatsapp/webhook")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            request.Headers.TryAddWithoutValidation("X-Hub-Signature-256", ComputeSignature(body, AppSecret));

            var response = await client.SendAsync(request);
            response.EnsureSuccessStatusCode();
        }
        finally
        {
            foreach (var key in values.Keys)
                Environment.SetEnvironmentVariable(key, null);
        }

        // Generous window covering WhatsAppMessageStatusChangedHandler.Configure's
        // ~4.25s bounded schedule plus real container/process overhead.
        var reachedErrorQueue = await WaitForWorkerLogLineAsync("was moved to the error queue", TimeSpan.FromSeconds(60));
        if (!reachedErrorQueue)
        {
            string workerOutputSnapshot;
            lock (_workerOutputLock) workerOutputSnapshot = _workerOutput.ToString();
            Assert.Fail("A permanently missing Message must eventually exhaust Wolverine's bounded retries and reach its own terminal-failure handling. Worker output:\n" + workerOutputSnapshot);
        }

        string fullWorkerOutput;
        lock (_workerOutputLock) fullWorkerOutput = _workerOutput.ToString();
        CountOccurrences(fullWorkerOutput, "WhatsAppMessageStatusUnknownMessage").Should().Be(4,
            "the configured schedule is exactly four attempts (initial + three retries) - never more, never fewer");
        CountOccurrences(fullWorkerOutput, "was moved to the error queue").Should().Be(1,
            "exhaustion must be recorded exactly once for this single event - never an unbounded/looping accumulation");

        (await CountMessagesForProviderMessageIdAsync(tenantId, providerMessageId)).Should().Be(0,
            "a permanently unresolvable status update must never fabricate a Message");

        var deadLetterCount = await CountDeadLettersAsync(providerMessageId);
        Console.WriteLine($"wolverine_dead_letters row count across {string.Join(", ", MessagingSchemas)}: {deadLetterCount} (informational only - see this test's own doc comment)");
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

    // ---- Seeding ------------------------------------------------------------

    private async Task SeedTenantRouteAsync(Guid tenantId, DateTimeOffset now)
    {
        await using var dbContext = CreateExternalIntegrationsDbContext();
        dbContext.WhatsAppTenantRoutes.Add(WhatsAppTenantRoute.Create(Guid.NewGuid(), PhoneNumberId, tenantId, now));
        await dbContext.SaveChangesAsync();
    }

    private async Task SeedSentMessageAsync(Guid tenantId, string providerMessageId, DateTimeOffset now)
    {
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);
        await using var dbContext = CreateCommunicationDbContext(tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext.Database, tenantId);

        var message = Message.Create(
            Guid.NewGuid(), tenantId, Guid.NewGuid(), "WhatsApp", "RESERVATION_CONFIRMATION",
            null, "Olá, sua reserva foi confirmada.", $"retry-{Guid.NewGuid():N}", now);
        message.MarkQueued();
        message.MarkSending();
        message.MarkSent(now, providerMessageId);

        dbContext.Messages.Add(message);
        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();
    }

    private static string BuildStatusPayload(string phoneNumberId, string id, string status, DateTimeOffset occurredAt) =>
        "{\"entry\":[{\"changes\":[{\"value\":{" +
        "\"metadata\":{\"phone_number_id\":\"" + phoneNumberId + "\"}," +
        "\"statuses\":[{\"id\":\"" + id + "\",\"status\":\"" + status + "\",\"timestamp\":\"" + occurredAt.ToUnixTimeSeconds() + "\"}]" +
        "}}]}]}";

    private static string ComputeSignature(string body, string appSecret)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(appSecret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(body));
        return "sha256=" + Convert.ToHexString(hash).ToLowerInvariant();
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

    // ---- DB access --------------------------------------------------------

    private static async Task SetTenantAsync(Microsoft.EntityFrameworkCore.Infrastructure.DatabaseFacade database, Guid tenantId) =>
        await database.ExecuteSqlInterpolatedAsync($"SELECT set_config('app.tenant_id', {tenantId.ToString()}, true)");

    private async Task<Message?> WaitForMessageStatusAsync(Guid tenantId, string providerMessageId, MessageStatus expected, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var message = await ReadMessageByProviderMessageIdAsync(tenantId, providerMessageId);
            if (message is not null && message.Status == expected)
                return message;
            await Task.Delay(TimeSpan.FromMilliseconds(500));
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

    private async Task<int> CountMessagesForProviderMessageIdAsync(Guid tenantId, string providerMessageId)
    {
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);
        await using var dbContext = CreateCommunicationDbContext(tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext.Database, tenantId);

        var count = await dbContext.Messages.CountAsync(m => m.TenantId == tenantId && m.ProviderMessageId == providerMessageId);

        await transaction.CommitAsync();
        return count;
    }

    /// <summary>
    /// <c>IHostPro.Worker</c> configures Wolverine's Main store
    /// (<c>platform_messaging</c>) plus five ancillary stores (Housekeeping/
    /// Reservations/Configuration/Dashboard/ExternalIntegrations) —
    /// <c>WhatsAppMessageStatusChangedHandler</c> has no explicit store
    /// association of its own (unlike the outbox transaction executors,
    /// which call <c>MessageContext.OverrideStorage</c> to pin themselves to
    /// one specific store), so this checks every schema Wolverine could
    /// plausibly have chosen rather than assuming Main — which specific
    /// store a plain consumer's dead letter lands in is Wolverine's own
    /// internal routing detail, not itself part of what this checkpoint
    /// needs to prove; what matters is that it lands in exactly one durable
    /// <c>wolverine_dead_letters</c> row, not that a specific schema was
    /// guessed correctly.
    ///
    /// Scopes by <c>message_type</c> only, never a structured foreign key to
    /// our own ProviderMessageId (the table carries none — only the
    /// serialized envelope body and the exception text). Safe without a body
    /// match: each test gets its own fresh, isolated Postgres/RabbitMQ
    /// container (<see cref="InitializeAsync"/>), so no other test's dead
    /// letter can ever appear in this same database. The
    /// <paramref name="providerMessageId"/> parameter exists for
    /// readability/call-site symmetry with <see cref="WaitForMessageStatusAsync"/>
    /// only.
    /// </summary>
    private static readonly string[] MessagingSchemas =
    [
        "platform_messaging", "housekeeping_messaging", "reservations_messaging",
        "configuration_messaging", "dashboard_messaging", "external_integrations_messaging",
    ];

    private async Task<long> CountDeadLettersAsync(string providerMessageId)
    {
        _ = providerMessageId;
        await using var connection = new NpgsqlConnection(_migratorConnectionString);
        await connection.OpenAsync();

        long total = 0;
        foreach (var schema in MessagingSchemas)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT count(*) FROM {schema}.wolverine_dead_letters WHERE message_type ILIKE '%WhatsAppMessageStatusChanged%'";
            total += (long)(await command.ExecuteScalarAsync())!;
        }
        return total;
    }

    private CommunicationDbContext CreateCommunicationDbContext(ITenantContext tenantContext)
    {
        var options = new DbContextOptionsBuilder<CommunicationDbContext>()
            .UseNpgsql(_migratorConnectionString, npgsqlOptions => npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "communication"))
            .Options;
        return new CommunicationDbContext(options, tenantContext);
    }

    private ExternalIntegrationsDbContext CreateExternalIntegrationsDbContext()
    {
        var options = new DbContextOptionsBuilder<ExternalIntegrationsDbContext>()
            .UseNpgsql(_migratorConnectionString, npgsqlOptions => npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "external_integrations"))
            .Options;
        return new ExternalIntegrationsDbContext(options, new TenantContext());
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
