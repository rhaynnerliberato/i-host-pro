using FluentAssertions;
using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Infrastructure.Messaging;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.BuildingBlocks.Infrastructure.Persistence;
using IHostPro.Contexts.ExternalIntegrations.Application;
using IHostPro.Contexts.ExternalIntegrations.Application.AirbnbEmailBridge;
using IHostPro.Contexts.ExternalIntegrations.Contracts;
using IHostPro.Contexts.ExternalIntegrations.Domain;
using IHostPro.Contexts.ExternalIntegrations.Infrastructure;
using IHostPro.Contexts.ExternalIntegrations.Infrastructure.Persistence;
using JasperFx;
using JasperFx.Resources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.Postgresql;
using Wolverine.RabbitMQ;

namespace IHostPro.Contexts.ExternalIntegrations.Tests.Integration;

/// <summary>
/// AIRBNB AUTO-PUBLISH REAL TRANSACTION REGRESSION PROOF (emergency gate,
/// opened mid-audit of Airbnb Email Operational Exception Resolution).
///
/// Static code tracing across <see cref="AirbnbEmailDeltaSyncRunner"/>
/// (Application), <c>AirbnbEmailUnitOfWork</c>, <c>ExternalIntegrationsOutboxTransactionExecutor</c>
/// and <c>AirbnbResolvedReservationSyncPublisher</c> (all Infrastructure)
/// strongly suggested that the real auto-publish path — the ONE path that
/// invokes <see cref="IAirbnbResolvedReservationSyncPublisher"/> from inside
/// an already-open unit-of-work transaction on the exact same scoped
/// <c>ExternalIntegrationsDbContext</c> — deterministically attempted a
/// SECOND transaction on that same DbContext via
/// <c>ExternalIntegrationsOutboxTransactionExecutor</c>, which
/// <see cref="TenantAwareTransactionScope"/> correctly rejected with
/// <see cref="NestedUnitOfWorkException"/>. No existing test could catch this
/// before this file: every unit test for this call chain fakes both the unit
/// of work and the transaction executor (never sharing a real DbContext), and
/// no prior integration test combined the real Wolverine outbox executor with
/// the real Airbnb Email transaction machinery at all.
///
/// This is the ONE focused, real-Postgres/real-RabbitMq/real-DI regression
/// suite the emergency gate asked for — real <see cref="ExternalIntegrationsDbContext"/>,
/// real <c>ExternalIntegrationsOutboxTransactionExecutor</c>, real
/// <c>AirbnbResolvedReservationSyncPublisher</c>, real DI registrations
/// (<see cref="ExternalIntegrationsModuleExtensions.AddExternalIntegrationsAirbnbEmailBridgeWorker"/>,
/// the exact method <c>IHostPro.Worker</c>'s own <c>Program.cs</c> uses), and
/// the actual <see cref="AirbnbEmailDeltaSyncRunner.RunAsync"/> execution path.
/// Only the two network-touching dependencies (<see cref="IAirbnbEmailAuthenticator"/>/
/// <see cref="IAirbnbEmailMessageSource"/>) are replaced with in-file fakes —
/// everything else, including the transaction/outbox machinery under
/// suspicion, is the real production wiring.
///
/// Every test here stops the shared RabbitMQ container BEFORE invoking the
/// runner and restarts it afterward — confirmed empirically (mirroring
/// <c>IdentityOutboxTransactionExecutorTests</c>'s own durability tests) that
/// with a REACHABLE broker, Wolverine's durability agent delivers a durable
/// envelope and removes it from <c>wolverine_outgoing_envelopes</c> almost
/// immediately, so a row-count assertion against that table is only reliable
/// while the broker is unreachable. This proves exactly what matters here —
/// that the receipt mutation and the outbox envelope are durably persisted
/// together, atomically, in the SAME transaction — independent of delivery
/// timing.
///
/// RED → GREEN evolution (evidence preserved per the emergency gate's own
/// instruction, not to be re-run manually): with the runner still depending
/// on <c>IAirbnbEmailUnitOfWork</c> for its transactional block, running
/// <see cref="RunAsync_auto_publish_path_commits_the_receipt_and_outbox_event_atomically_in_one_transaction"/>
/// observed exactly one logged <see cref="NestedUnitOfWorkException"/>, the
/// receipt committed as <c>Failed</c>/<c>"PublisherFailure"</c>, and zero
/// outbox envelopes — reported verbatim to the gate owner before any
/// production code changed. The fix (this same commit) makes
/// <see cref="AirbnbEmailDeltaSyncRunner"/> use
/// <c>IExternalIntegrationsTransactionExecutor</c> exclusively and makes
/// <c>AirbnbResolvedReservationSyncPublisher</c> stop opening its own
/// transaction — the assertions below now express the INTENDED, fixed
/// behavior and are GREEN against the fixed production code.
/// </summary>
public sealed class AirbnbAutoPublishNestedTransactionRegressionTests : IClassFixture<AirbnbAutoPublishNestedTransactionRegressionTests.Fixture>
{
    private const string OutboxSchema = "external_integrations_messaging";

    // Deliberately reuses the exact synthetic "supported parser v1 input"
    // fixture already proven against the real AirbnbReservationReminderParser
    // in AirbnbReservationReminderParserTests — entirely fabricated
    // placeholder text (Fase 9 review), no real guest/listing/reservation
    // data from any real mailbox.
    private const string ReservationReminderSubject = "Lembrete de reserva: Hóspede Teste chega em breve!";

    private const string ReservationReminderBody = """
        <html><body>
        <div>
        <p>Studio Exemplo Fixture</p>
        <p>Casa/apto inteiro</p>
        <table>
        <tr><td>Check-in</td><td>Checkout</td></tr>
        <tr><td>qui., 5 de nov.<br/>14:00</td><td>dom., 8 de nov.<br/>11:00</td></tr>
        </table>
        <p>Hóspedes</p>
        <p>2 adultos</p>
        <p>Código de confirmação</p>
        <p>TESTCODE12</p>
        </div>
        </body></html>
        """;

    private const string ListingTitle = "Studio Exemplo Fixture";
    private static readonly DateTimeOffset MessageReceivedAtUtc = new(2026, 11, 3, 9, 0, 0, TimeSpan.Zero);

    private readonly Fixture _fixture;
    private readonly string _migratorConnectionString;
    private readonly string _appConnectionString;

    public AirbnbAutoPublishNestedTransactionRegressionTests(Fixture fixture)
    {
        _fixture = fixture;
        _migratorConnectionString = fixture.MigratorConnectionString;
        _appConnectionString = fixture.AppConnectionString;
    }

    public sealed class Fixture : IAsyncLifetime
    {
        private const string AppRolePassword = "test_app_password";
        private const string MigratorRolePassword = "test_migrator_password";

        private PostgreSqlContainer _postgresContainer = null!;
        public RabbitMqContainer RabbitMqContainer { get; private set; } = null!;
        public string MigratorConnectionString { get; private set; } = null!;
        public string AppConnectionString { get; private set; } = null!;

        public async Task InitializeAsync()
        {
            _postgresContainer = new PostgreSqlBuilder()
                .WithImage("postgres:16")
                .WithDatabase("ihostpro_test")
                .WithUsername("ihostpro")
                .WithPassword("ihostpro_dev")
                .Build();
            // Dynamic host port (no WithPortBinding) — deliberately avoids the
            // fixed-port collision the standing dev ihostpro-rabbitmq container
            // causes for other fixtures in this repo that hardcode 5672.
            RabbitMqContainer = new RabbitMqBuilder().WithImage("rabbitmq:3-management-alpine").Build();

            await Task.WhenAll(_postgresContainer.StartAsync(), RabbitMqContainer.StartAsync());

            var adminConnectionString = _postgresContainer.GetConnectionString();

            await using (var adminConnection = new NpgsqlConnection(adminConnectionString))
            {
                await adminConnection.OpenAsync();
                await ExecuteAsync(adminConnection, $"""
                    CREATE ROLE ihostpro_migrator LOGIN PASSWORD '{MigratorRolePassword}';
                    CREATE ROLE ihostpro_app LOGIN PASSWORD '{AppRolePassword}';
                    GRANT CREATE ON DATABASE ihostpro_test TO ihostpro_migrator;
                    """);
            }

            var builder = new NpgsqlConnectionStringBuilder(adminConnectionString);
            builder.Username = "ihostpro_migrator";
            builder.Password = MigratorRolePassword;
            MigratorConnectionString = builder.ConnectionString;
            builder.Username = "ihostpro_app";
            builder.Password = AppRolePassword;
            AppConnectionString = builder.ConnectionString;

            await using (var migratorDbContext = CreateDbContext(MigratorConnectionString, new TenantContext()))
            {
                await migratorDbContext.Database.MigrateAsync();
            }

            await ProvisionOutboxAsMigratorAsync();
        }

        public async Task DisposeAsync()
        {
            await _postgresContainer.DisposeAsync();
            await RabbitMqContainer.DisposeAsync();
        }

        private async Task ProvisionOutboxAsMigratorAsync()
        {
            var hostBuilder = Host.CreateApplicationBuilder();
            hostBuilder.UseWolverine(opts =>
            {
                opts.EnrollAncillaryPostgresqlOutbox(MigratorConnectionString, OutboxSchema, typeof(ExternalIntegrationsDbContext));
                opts.AutoBuildMessageStorageOnStartup = AutoCreate.None;
                opts.UseEntityFrameworkCoreTransactions();
            });

            using (var outboxHost = hostBuilder.Build())
            {
                await outboxHost.SetupResources();
            }

            await using var connection = new NpgsqlConnection(MigratorConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                GRANT USAGE ON SCHEMA {OutboxSchema} TO ihostpro_app;
                GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA {OutboxSchema} TO ihostpro_app;
                GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA {OutboxSchema} TO ihostpro_app;
                ALTER DEFAULT PRIVILEGES FOR ROLE ihostpro_migrator IN SCHEMA {OutboxSchema}
                  GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO ihostpro_app;
                ALTER DEFAULT PRIVILEGES FOR ROLE ihostpro_migrator IN SCHEMA {OutboxSchema}
                  GRANT USAGE, SELECT ON SEQUENCES TO ihostpro_app;
                """;
            await command.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task RunAsync_auto_publish_path_commits_the_receipt_and_outbox_event_atomically_in_one_transaction()
    {
        // ---- Arrange: a real, seeded, auto-publish-enabled mailbox connection + listing mapping ----
        var tenantId = Guid.NewGuid();
        var propertyId = Guid.NewGuid();
        const string mailFolderId = "inbox";
        const string graphMessageId = "regression-proof-msg-1";

        var autoPublishCutoff = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero); // well before MessageReceivedAtUtc
        await SeedConnectedAutoPublishMailboxAsync(tenantId, propertyId, autoPublishCutoff);

        var message = new AirbnbEmailMessageSummary(
            graphMessageId, null, MessageReceivedAtUtc, ReservationReminderSubject,
            "automated@airbnb.com", "automated@airbnb.com", "preview");
        var messageSource = new SingleMessageAirbnbEmailMessageSource(message, ReservationReminderBody);
        var capturingLoggerProvider = new CapturingLoggerProvider();

        var host = await BuildHostAsync(_fixture.RabbitMqContainer, messageSource, capturingLoggerProvider);
        try
        {
            // Unreachable broker — see the class doc comment — so the durable
            // envelope stays observably "pending" instead of being delivered
            // and removed before this test can assert on it.
            await _fixture.RabbitMqContainer.StopAsync();

            // ---- Act: the actual real Worker execution path — no fakes below IAirbnbEmailAuthenticator/IAirbnbEmailMessageSource ----
            using var scope = host.Services.CreateScope();
            scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(tenantId);
            var runner = scope.ServiceProvider.GetRequiredService<IAirbnbEmailDeltaSyncRunner>();

            var act = async () => await runner.RunAsync(tenantId, mailFolderId, CancellationToken.None);

            await act.Should().NotThrowAsync("the fixed auto-publish path completes successfully end-to-end, even with the broker unreachable — durability never depends on delivery");

            // ---- Assert: exact evidence required by the emergency gate's GREEN-path proof ----

            // NestedUnitOfWorkExceptionObserved=false — no error of any kind was
            // logged by the runner for this receipt's publish attempt.
            capturingLoggerProvider.LoggedExceptions
                .Where(e => e.Category.Contains(nameof(AirbnbEmailDeltaSyncRunner)))
                .Should().BeEmpty("the fixed publisher no longer opens a second transaction, so no NestedUnitOfWorkException (or any other exception) is ever caught");

            // ReceiptFinalStatus=Processed, PublisherFailure=false.
            var receipt = await GetReceiptAsync(tenantId, graphMessageId);
            receipt.ProcessingStatus.Should().Be(
                AirbnbEmailMessageProcessingStatus.Processed,
                "the real auto-publish path now completes successfully instead of falling into the generic catch");
            receipt.FailureReason.Should().BeNull("a successful publish never records a failure reason");

            // OutboxMessageCreated=true / AirbnbReservationImportedPersisted=true.
            (await CountAirbnbReservationImportedEnvelopesAsync(tenantId)).Should().Be(1,
                "the receipt mutation and the outbox envelope now commit together in the SAME transaction owned by IExternalIntegrationsTransactionExecutor");
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
            await _fixture.RabbitMqContainer.StartAsync();
        }
    }

    /// <summary>
    /// Idempotency — mandatory per the emergency gate's fix-approval directive
    /// (item 10): the exact same Graph message observed a second time (e.g. a
    /// delta replay) must never duplicate the receipt or the outbox envelope.
    /// The layer that guarantees this is <see cref="IAirbnbEmailMessageReceiptRepository.ExistsForCurrentTenantAsync"/>
    /// — backed by the real unique index on (tenant_id, graph_message_id)
    /// already proven by <c>ExternalIntegrationsFoundationTests</c> — which
    /// makes <see cref="AirbnbEmailDeltaSyncRunner.RunAsync"/> skip a message
    /// whose receipt already exists before ever re-evaluating or
    /// re-publishing it.
    /// </summary>
    [Fact]
    public async Task RunAsync_processing_the_same_message_twice_does_not_duplicate_the_receipt_or_the_outbox_event()
    {
        var tenantId = Guid.NewGuid();
        var propertyId = Guid.NewGuid();
        const string mailFolderId = "inbox";
        const string graphMessageId = "regression-proof-idempotency-msg-1";

        var autoPublishCutoff = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        await SeedConnectedAutoPublishMailboxAsync(tenantId, propertyId, autoPublishCutoff);

        var message = new AirbnbEmailMessageSummary(
            graphMessageId, null, MessageReceivedAtUtc, ReservationReminderSubject,
            "automated@airbnb.com", "automated@airbnb.com", "preview");
        var messageSource = new SingleMessageAirbnbEmailMessageSource(message, ReservationReminderBody);
        var capturingLoggerProvider = new CapturingLoggerProvider();

        var host = await BuildHostAsync(_fixture.RabbitMqContainer, messageSource, capturingLoggerProvider);
        try
        {
            await _fixture.RabbitMqContainer.StopAsync();

            using var scope = host.Services.CreateScope();
            scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(tenantId);
            var runner = scope.ServiceProvider.GetRequiredService<IAirbnbEmailDeltaSyncRunner>();

            await runner.RunAsync(tenantId, mailFolderId, CancellationToken.None);
            var receiptCountAfterFirst = await CountReceiptsAsync(tenantId, graphMessageId);
            var envelopeCountAfterFirst = await CountAirbnbReservationImportedEnvelopesAsync(tenantId);

            // Same delta cursor, same message source — mirrors a real delta
            // replay observing the same Graph message again.
            await runner.RunAsync(tenantId, mailFolderId, CancellationToken.None);
            var receiptCountAfterSecond = await CountReceiptsAsync(tenantId, graphMessageId);
            var envelopeCountAfterSecond = await CountAirbnbReservationImportedEnvelopesAsync(tenantId);

            receiptCountAfterFirst.Should().Be(1);
            receiptCountAfterSecond.Should().Be(1, "ExistsForCurrentTenantAsync must skip an already-receipted message on replay");
            envelopeCountAfterFirst.Should().Be(1);
            envelopeCountAfterSecond.Should().Be(1, "no duplicate AirbnbReservationImported envelope may ever be created for the same Graph message");
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
            await _fixture.RabbitMqContainer.StartAsync();
        }
    }

    /// <summary>
    /// Historical cutoff safety — mandatory per the emergency gate's
    /// fix-approval directive (item 12): the fix must not broaden
    /// auto-publish semantics. A message received BEFORE
    /// <see cref="AirbnbEmailMailboxConnection.AutoPublishNotBeforeUtc"/>
    /// must still be classified <c>Ignored</c> and must never reach
    /// <see cref="IAirbnbResolvedReservationSyncPublisher"/> at all — unrelated
    /// to the transaction fix, since <see cref="AirbnbEmailDeltaSyncRunner.ApplyDryRunOutcomeAsync"/>
    /// branches to <c>MarkIgnored</c> before the publisher is ever called.
    /// </summary>
    [Fact]
    public async Task RunAsync_a_message_before_the_auto_publish_cutoff_is_ignored_and_never_reaches_the_publisher()
    {
        var tenantId = Guid.NewGuid();
        var propertyId = Guid.NewGuid();
        const string mailFolderId = "inbox";
        const string graphMessageId = "regression-proof-historical-cutoff-msg-1";

        // Cutoff is AFTER the message's own ReceivedAtUtc — the opposite of
        // the other tests in this file, which all use a cutoff safely before it.
        var autoPublishCutoff = MessageReceivedAtUtc.AddDays(1);
        await SeedConnectedAutoPublishMailboxAsync(tenantId, propertyId, autoPublishCutoff);

        var message = new AirbnbEmailMessageSummary(
            graphMessageId, null, MessageReceivedAtUtc, ReservationReminderSubject,
            "automated@airbnb.com", "automated@airbnb.com", "preview");
        var messageSource = new SingleMessageAirbnbEmailMessageSource(message, ReservationReminderBody);
        var capturingLoggerProvider = new CapturingLoggerProvider();

        var host = await BuildHostAsync(_fixture.RabbitMqContainer, messageSource, capturingLoggerProvider);
        try
        {
            await _fixture.RabbitMqContainer.StopAsync();

            using var scope = host.Services.CreateScope();
            scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(tenantId);
            var runner = scope.ServiceProvider.GetRequiredService<IAirbnbEmailDeltaSyncRunner>();

            await runner.RunAsync(tenantId, mailFolderId, CancellationToken.None);

            var receipt = await GetReceiptAsync(tenantId, graphMessageId);
            receipt.ProcessingStatus.Should().Be(
                AirbnbEmailMessageProcessingStatus.Ignored,
                "AutoPublishNotBeforeUtc must still protect pre-cutoff messages from a real publish after the transaction fix");

            (await CountAirbnbReservationImportedEnvelopesAsync(tenantId)).Should().Be(0,
                "the resolved publisher must never be invoked for a message the historical-cutoff guard classifies Ignored");
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
            await _fixture.RabbitMqContainer.StartAsync();
        }
    }

    private async Task SeedConnectedAutoPublishMailboxAsync(Guid tenantId, Guid propertyId, DateTimeOffset autoPublishNotBeforeUtc)
    {
        var seedTenantContext = new TenantContext();
        seedTenantContext.SetTenant(tenantId);
        await using var seedDbContext = CreateDbContext(_migratorConnectionString, seedTenantContext);
        await using var seedTransaction = await seedDbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(seedDbContext, tenantId);

        var connection = AirbnbEmailMailboxConnection.Create(Guid.NewGuid(), tenantId, DateTimeOffset.UtcNow);
        connection.Connect("home-account-1", "entra-tenant-1", "guest@hotmail.com", "Mail.Read", DateTimeOffset.UtcNow);
        connection.EnableAutoPublish(autoPublishNotBeforeUtc, DateTimeOffset.UtcNow);
        seedDbContext.AirbnbEmailMailboxConnections.Add(connection);

        seedDbContext.AirbnbListingTitleMappings.Add(
            AirbnbListingTitleMapping.Create(Guid.NewGuid(), tenantId, ListingTitle, propertyId, DateTimeOffset.UtcNow));

        await seedDbContext.SaveChangesAsync();
        await seedTransaction.CommitAsync();
    }

    private async Task<AirbnbEmailMessageReceipt> GetReceiptAsync(Guid tenantId, string graphMessageId)
    {
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);
        await using var dbContext = CreateDbContext(_migratorConnectionString, tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);
        return await dbContext.AirbnbEmailMessageReceipts.AsNoTracking().SingleAsync(r => r.GraphMessageId == graphMessageId);
    }

    private async Task<int> CountReceiptsAsync(Guid tenantId, string graphMessageId)
    {
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);
        await using var dbContext = CreateDbContext(_migratorConnectionString, tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);
        return await dbContext.AirbnbEmailMessageReceipts.CountAsync(r => r.GraphMessageId == graphMessageId);
    }

    // ---- Real Worker-equivalent host: mirrors IHostPro.Worker's own Program.cs registration exactly ----

    private async Task<IHost> BuildHostAsync(
        RabbitMqContainer rabbitMqContainer, IAirbnbEmailMessageSource messageSource, CapturingLoggerProvider capturingLoggerProvider)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:ExternalIntegrations"] = _appConnectionString,
            ["ExternalIntegrations:AirbnbEmailBridge:TokenCacheEncryptionKeyBase64"] =
                Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)),
        }).Build();

        var hostBuilder = Host.CreateApplicationBuilder();
        hostBuilder.Logging.AddProvider(capturingLoggerProvider);

        hostBuilder.Services.AddScoped<ITenantContext, TenantContext>();
        hostBuilder.Services.AddScoped<ICurrentTenantProvider, TenantContextCurrentTenantProvider>();
        hostBuilder.Services.AddIHostProTenantAwarePipeline();

        // The exact real registration IHostPro.Worker's own Program.cs calls —
        // wires the delta sync runner, ExternalIntegrationsOutboxTransactionExecutor
        // and AirbnbResolvedReservationSyncPublisher together in this one scope,
        // exactly as production does.
        hostBuilder.Services.AddExternalIntegrationsAirbnbEmailBridgeWorker(configuration);

        // Override only the two network-touching dependencies — everything else
        // (DbContext, unit of work, outbox executor, publisher, repositories,
        // real parser, real dry-run evaluator) stays the real production wiring.
        hostBuilder.Services.AddScoped<IAirbnbEmailAuthenticator, FakeAirbnbEmailAuthenticator>();
        hostBuilder.Services.AddSingleton(messageSource);

        hostBuilder.UseWolverine(opts =>
        {
            opts.UseRabbitMq(rabbit =>
            {
                rabbit.HostName = rabbitMqContainer.Hostname;
                rabbit.Port = rabbitMqContainer.GetMappedPublicPort(5672);
                rabbit.UserName = RabbitMqBuilder.DefaultUsername;
                rabbit.Password = RabbitMqBuilder.DefaultPassword;
            });

            opts.EnrollAncillaryPostgresqlOutbox(_appConnectionString, OutboxSchema, typeof(ExternalIntegrationsDbContext));
            opts.AutoBuildMessageStorageOnStartup = AutoCreate.None;
            opts.UseEntityFrameworkCoreTransactions();

            // Mirrors IHostPro.Worker's Program.cs sender-side routing rule for
            // AirbnbReservationImported exactly (same exchange type/durable
            // outbox shape), on a test-only exchange name.
            opts.PublishMessage(typeof(AirbnbReservationImported))
                .ToRabbitRoutingKey("external-integrations-events-test", "airbnb_reservation_imported", exchange => exchange.ExchangeType = ExchangeType.Topic)
                .UseDurableOutbox();
        });

        var host = hostBuilder.Build();
        await host.StartAsync();
        return host;
    }

    // ---- Fakes for the two network-touching dependencies only ----

    private sealed class FakeAirbnbEmailAuthenticator : IAirbnbEmailAuthenticator
    {
        public Task<AirbnbEmailAuthenticationOutcome> ConnectInteractiveAsync(Guid tenantId, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not exercised — this regression test seeds an already-Connected connection.");

        public Task<AirbnbEmailSilentAcquisitionOutcome> AcquireTokenSilentAsync(Guid tenantId, CancellationToken cancellationToken) =>
            Task.FromResult(AirbnbEmailSilentAcquisitionOutcome.Success("fake-access-token"));
    }

    private sealed class SingleMessageAirbnbEmailMessageSource(AirbnbEmailMessageSummary message, string body) : IAirbnbEmailMessageSource
    {
        public Task<AirbnbEmailDeltaFetchOutcome> GetDeltaPageAsync(
            string accessToken, string mailFolderId, string? deltaOrNextLink, CancellationToken cancellationToken) =>
            Task.FromResult(AirbnbEmailDeltaFetchOutcome.Success(
                new AirbnbEmailDeltaPage([message], null, "https://fake.local/delta-1")));

        public Task<AirbnbEmailMessageContent?> GetMessageContentAsync(string accessToken, string messageId, CancellationToken cancellationToken) =>
            Task.FromResult(messageId == message.MessageId ? new AirbnbEmailMessageContent(message.Subject, body) : null);
    }

    /// <summary>Captures every logged exception (with its logger category) across the whole test host, so the test can assert on the EXACT exception type the runner caught — never inferred from side effects alone.</summary>
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public List<(string Category, Exception Exception)> LoggedExceptions { get; } = [];

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(this, categoryName);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(CapturingLoggerProvider owner, string categoryName) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (exception is not null)
                    owner.LoggedExceptions.Add((categoryName, exception));
            }
        }
    }

    // ---- Helpers ----

    /// <summary>
    /// Counts ONLY this test's own <c>AirbnbReservationImported</c> envelopes,
    /// identified by tenant id inside the raw envelope body — never a bare
    /// table count. This class's Postgres/RabbitMQ fixture is shared across
    /// every test method (<see cref="IClassFixture{TFixture}"/>), so a raw
    /// count would be thrown off by sibling tests' own envelopes; mirrors
    /// <c>IdentityOutboxTransactionExecutorTests.EnvelopeForTenantIsPendingAsync</c>'s
    /// own byte-pattern search for the identical reason.
    /// </summary>
    private async Task<long> CountAirbnbReservationImportedEnvelopesAsync(Guid tenantId)
    {
        await using var connection = new NpgsqlConnection(_migratorConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT count(*) FROM {OutboxSchema}.wolverine_outgoing_envelopes
            WHERE message_type ILIKE '%airbnbreservationimported%'
              AND position(convert_to(@tenantId, 'UTF8') in body) > 0
            """;
        command.Parameters.AddWithValue("tenantId", tenantId.ToString("D"));
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task SetTenantAsync(ExternalIntegrationsDbContext dbContext, Guid tenantId) =>
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.tenant_id', {tenantId.ToString()}, true)");

    private static ExternalIntegrationsDbContext CreateDbContext(string connectionString, ITenantContext tenantContext)
    {
        var options = new DbContextOptionsBuilder<ExternalIntegrationsDbContext>()
            .UseNpgsql(connectionString, npgsqlOptions =>
                npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "external_integrations"))
            .Options;

        return new ExternalIntegrationsDbContext(options, tenantContext);
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
