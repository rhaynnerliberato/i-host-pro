using FluentAssertions;
using IHostPro.Contexts.ExternalIntegrations.Application.AirbnbEmailBridge;
using IHostPro.Contexts.ExternalIntegrations.Application.AirbnbReservationParser;
using IHostPro.Contexts.ExternalIntegrations.Domain;
using IHostPro.Contexts.ExternalIntegrations.Tests.Unit.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace IHostPro.Contexts.ExternalIntegrations.Tests.Unit.Application.AirbnbEmailBridge;

public class AirbnbEmailDeltaSyncRunnerTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private const string MailFolderId = "inbox";

    private sealed record Harness(
        FakeAirbnbEmailMailboxConnectionRepository ConnectionRepository,
        FakeAirbnbEmailSyncStateRepository SyncStateRepository,
        FakeAirbnbEmailMessageReceiptRepository ReceiptRepository,
        FakeAirbnbEmailAuthenticator Authenticator,
        FakeAirbnbEmailMessageSource MessageSource,
        FakeAirbnbResolvedReservationSyncPublisher ResolvedPublisher,
        AirbnbEmailDeltaSyncRunner Runner);

    private static Harness BuildHarness(AirbnbEmailMailboxConnection? connection, params AirbnbEmailDeltaFetchOutcome[] pageOutcomes) =>
        BuildHarness(connection, new FakeAirbnbReservationDryRunEvaluator(), new FakeAirbnbResolvedReservationSyncPublisher(), pageOutcomes);

    private static Harness BuildHarness(
        AirbnbEmailMailboxConnection? connection, FakeAirbnbReservationDryRunEvaluator evaluator,
        FakeAirbnbResolvedReservationSyncPublisher resolvedPublisher, params AirbnbEmailDeltaFetchOutcome[] pageOutcomes)
    {
        var connectionRepository = FakeAirbnbEmailMailboxConnectionRepository.WithExisting(connection);
        var syncStateRepository = new FakeAirbnbEmailSyncStateRepository();
        var receiptRepository = new FakeAirbnbEmailMessageReceiptRepository();
        var authenticator = FakeAirbnbEmailAuthenticator.Succeeding(connectionRepository);
        var messageSource = new FakeAirbnbEmailMessageSource(pageOutcomes);

        var runner = new AirbnbEmailDeltaSyncRunner(
            connectionRepository, syncStateRepository, receiptRepository, authenticator, messageSource,
            evaluator, resolvedPublisher, new PassThroughExternalIntegrationsTransactionExecutor(), TimeProvider.System,
            NullLogger<AirbnbEmailDeltaSyncRunner>.Instance);

        return new Harness(connectionRepository, syncStateRepository, receiptRepository, authenticator, messageSource, resolvedPublisher, runner);
    }

    private static AirbnbEmailMailboxConnection ConnectedConnection()
    {
        var connection = AirbnbEmailMailboxConnection.Create(Guid.NewGuid(), TenantId, DateTimeOffset.UtcNow);
        connection.Connect("home-account-1", null, "guest@hotmail.com", "Mail.Read", DateTimeOffset.UtcNow);
        return connection;
    }

    /// <summary>Automatic Publication Design + Safety gate - a connection with auto-publish already turned on and a fixed cutoff, for the new publication-decision tests.</summary>
    private static AirbnbEmailMailboxConnection ConnectedConnectionWithAutoPublish(DateTimeOffset notBeforeUtc)
    {
        var connection = ConnectedConnection();
        connection.EnableAutoPublish(notBeforeUtc, DateTimeOffset.UtcNow);
        return connection;
    }

    private static AirbnbReservationDryRunOutcome ReadyOutcome(Guid propertyId, string externalReservationId = "TESTCODE12") =>
        AirbnbReservationDryRunOutcome.Ready(
            propertyId, externalReservationId, "Hospede Teste",
            new DateTimeOffset(2026, 11, 5, 14, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 11, 8, 11, 0, 0, TimeSpan.Zero), 2);

    [Fact]
    public async Task No_connection_is_a_no_op_and_never_calls_the_message_source()
    {
        var harness = BuildHarness(connection: null);

        await harness.Runner.RunAsync(TenantId, MailFolderId, CancellationToken.None);

        harness.MessageSource.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task A_disabled_connection_is_a_no_op()
    {
        var connection = ConnectedConnection();
        connection.Disconnect(DateTimeOffset.UtcNow); // IsEnabled=false, AuthorizationStatus=Disconnected
        var harness = BuildHarness(connection);

        await harness.Runner.RunAsync(TenantId, MailFolderId, CancellationToken.None);

        harness.MessageSource.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Silent_auth_requiring_reauthorization_marks_the_connection_and_never_calls_the_message_source()
    {
        var connection = ConnectedConnection();
        var harness = BuildHarness(connection);
        harness.Authenticator.SilentOutcome = AirbnbEmailSilentAcquisitionOutcome.Failure(reauthorizationRequired: true);

        await harness.Runner.RunAsync(TenantId, MailFolderId, CancellationToken.None);

        harness.MessageSource.Calls.Should().BeEmpty();
        connection.AuthorizationStatus.Should().Be(AirbnbEmailAuthorizationStatus.Error);
    }

    [Fact]
    public async Task Transient_silent_auth_failure_does_not_mark_the_connection_and_stops_cleanly()
    {
        var connection = ConnectedConnection();
        var harness = BuildHarness(connection);
        harness.Authenticator.SilentOutcome = AirbnbEmailSilentAcquisitionOutcome.Failure(reauthorizationRequired: false);

        await harness.Runner.RunAsync(TenantId, MailFolderId, CancellationToken.None);

        harness.MessageSource.Calls.Should().BeEmpty();
        connection.AuthorizationStatus.Should().Be(AirbnbEmailAuthorizationStatus.Connected, "a transient failure must not be treated as a permanent error");
    }

    [Fact]
    public async Task Initial_sync_passes_a_null_cursor_and_creates_a_sync_state_row()
    {
        var connection = ConnectedConnection();
        var harness = BuildHarness(connection, AirbnbEmailDeltaFetchOutcome.Success(
            new AirbnbEmailDeltaPage([], null, "https://graph.microsoft.com/v1.0/delta-1")));

        await harness.Runner.RunAsync(TenantId, MailFolderId, CancellationToken.None);

        harness.MessageSource.Calls.Should().ContainSingle();
        harness.MessageSource.Calls[0].Cursor.Should().BeNull();
        harness.SyncStateRepository.Current.Should().NotBeNull();
        harness.SyncStateRepository.Current!.DeltaLink.Should().Be("https://graph.microsoft.com/v1.0/delta-1");
    }

    [Fact]
    public async Task A_subsequent_sync_passes_the_persisted_deltaLink_verbatim()
    {
        var connection = ConnectedConnection();
        var connectionRepository = FakeAirbnbEmailMailboxConnectionRepository.WithExisting(connection);
        var syncStateRepository = new FakeAirbnbEmailSyncStateRepository();
        var existingState = AirbnbEmailSyncState.Create(Guid.NewGuid(), TenantId, connection.Id, MailFolderId, DateTimeOffset.UtcNow);
        existingState.RecordSuccess("https://graph.microsoft.com/v1.0/existing-cursor", DateTimeOffset.UtcNow);
        syncStateRepository.Add(existingState);

        var receiptRepository = new FakeAirbnbEmailMessageReceiptRepository();
        var authenticator = FakeAirbnbEmailAuthenticator.Succeeding(connectionRepository);
        var messageSource = new FakeAirbnbEmailMessageSource(
            AirbnbEmailDeltaFetchOutcome.Success(new AirbnbEmailDeltaPage([], null, "https://graph.microsoft.com/v1.0/new-cursor")));
        var runner = new AirbnbEmailDeltaSyncRunner(
            connectionRepository, syncStateRepository, receiptRepository, authenticator, messageSource,
            new FakeAirbnbReservationDryRunEvaluator(), new FakeAirbnbResolvedReservationSyncPublisher(), new PassThroughExternalIntegrationsTransactionExecutor(), TimeProvider.System,
            NullLogger<AirbnbEmailDeltaSyncRunner>.Instance);

        await runner.RunAsync(TenantId, MailFolderId, CancellationToken.None);

        messageSource.Calls.Should().ContainSingle();
        messageSource.Calls[0].Cursor.Should().Be("https://graph.microsoft.com/v1.0/existing-cursor");
    }

    [Fact]
    public async Task Observed_messages_become_receipts()
    {
        var connection = ConnectedConnection();
        var messages = new[]
        {
            new AirbnbEmailMessageSummary("msg-1", "<msg-1@mail>", DateTimeOffset.UtcNow, "Reservation", "a@airbnb.com", "a@airbnb.com", "preview"),
            new AirbnbEmailMessageSummary("msg-2", "<msg-2@mail>", DateTimeOffset.UtcNow, "Cancellation", "a@airbnb.com", "a@airbnb.com", "preview"),
        };
        var harness = BuildHarness(connection, AirbnbEmailDeltaFetchOutcome.Success(
            new AirbnbEmailDeltaPage(messages, null, "https://graph.microsoft.com/v1.0/delta-1")));

        await harness.Runner.RunAsync(TenantId, MailFolderId, CancellationToken.None);

        harness.ReceiptRepository.Added.Should().HaveCount(2);
        harness.ReceiptRepository.Added.Select(r => r.GraphMessageId).Should().BeEquivalentTo("msg-1", "msg-2");
        harness.ReceiptRepository.Added.Should().OnlyContain(r => r.ProcessingStatus == AirbnbEmailMessageProcessingStatus.Pending);
    }

    [Fact]
    public async Task A_message_already_receipted_is_never_duplicated_on_replay()
    {
        var connection = ConnectedConnection();
        var connectionRepository = FakeAirbnbEmailMailboxConnectionRepository.WithExisting(connection);
        var syncStateRepository = new FakeAirbnbEmailSyncStateRepository();
        var receiptRepository = new FakeAirbnbEmailMessageReceiptRepository();
        receiptRepository.Add(AirbnbEmailMessageReceipt.Create(
            Guid.NewGuid(), TenantId, "msg-1", null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        var authenticator = FakeAirbnbEmailAuthenticator.Succeeding(connectionRepository);
        var message = new AirbnbEmailMessageSummary("msg-1", null, DateTimeOffset.UtcNow, null, null, null, null);
        var messageSource = new FakeAirbnbEmailMessageSource(
            AirbnbEmailDeltaFetchOutcome.Success(new AirbnbEmailDeltaPage([message], null, "https://graph.microsoft.com/v1.0/delta-1")));
        var runner = new AirbnbEmailDeltaSyncRunner(
            connectionRepository, syncStateRepository, receiptRepository, authenticator, messageSource,
            new FakeAirbnbReservationDryRunEvaluator(), new FakeAirbnbResolvedReservationSyncPublisher(), new PassThroughExternalIntegrationsTransactionExecutor(), TimeProvider.System,
            NullLogger<AirbnbEmailDeltaSyncRunner>.Instance);

        await runner.RunAsync(TenantId, MailFolderId, CancellationToken.None);

        receiptRepository.Added.Should().HaveCount(1, "a message already receipted must never be inserted a second time");
    }

    [Fact]
    public async Task Paging_follows_nextLink_until_the_final_deltaLink_and_advances_the_cursor_only_at_the_end()
    {
        var connection = ConnectedConnection();
        var harness = BuildHarness(
            connection,
            AirbnbEmailDeltaFetchOutcome.Success(new AirbnbEmailDeltaPage([], "https://graph.microsoft.com/v1.0/page-2", null)),
            AirbnbEmailDeltaFetchOutcome.Success(new AirbnbEmailDeltaPage([], null, "https://graph.microsoft.com/v1.0/final-delta")));

        await harness.Runner.RunAsync(TenantId, MailFolderId, CancellationToken.None);

        harness.MessageSource.Calls.Should().HaveCount(2);
        harness.MessageSource.Calls[0].Cursor.Should().BeNull();
        harness.MessageSource.Calls[1].Cursor.Should().Be("https://graph.microsoft.com/v1.0/page-2");
        harness.SyncStateRepository.Current!.DeltaLink.Should().Be("https://graph.microsoft.com/v1.0/final-delta");
    }

    [Fact]
    public async Task A_failure_on_a_later_page_never_advances_the_cursor()
    {
        var connection = ConnectedConnection();
        var harness = BuildHarness(
            connection,
            AirbnbEmailDeltaFetchOutcome.Success(new AirbnbEmailDeltaPage([], "https://graph.microsoft.com/v1.0/page-2", null)),
            AirbnbEmailDeltaFetchOutcome.Failure(AirbnbEmailDeltaFetchFailureReason.TransientFailure, "http_500"));

        await harness.Runner.RunAsync(TenantId, MailFolderId, CancellationToken.None);

        harness.SyncStateRepository.Current!.DeltaLink.Should().BeNull("the run failed before observing the final page - the cursor must stay exactly where it was");
        harness.SyncStateRepository.Current.LastErrorCode.Should().Be("http_500");
    }

    [Fact]
    public async Task An_invalid_deltaLink_failure_resets_the_sync_state_instead_of_recording_a_generic_failure()
    {
        var connection = ConnectedConnection();
        var connectionRepository = FakeAirbnbEmailMailboxConnectionRepository.WithExisting(connection);
        var syncStateRepository = new FakeAirbnbEmailSyncStateRepository();
        var existingState = AirbnbEmailSyncState.Create(Guid.NewGuid(), TenantId, connection.Id, MailFolderId, DateTimeOffset.UtcNow);
        existingState.RecordSuccess("https://graph.microsoft.com/v1.0/stale-cursor", DateTimeOffset.UtcNow);
        syncStateRepository.Add(existingState);
        var receiptRepository = new FakeAirbnbEmailMessageReceiptRepository();
        var authenticator = FakeAirbnbEmailAuthenticator.Succeeding(connectionRepository);
        var messageSource = new FakeAirbnbEmailMessageSource(
            AirbnbEmailDeltaFetchOutcome.Failure(AirbnbEmailDeltaFetchFailureReason.InvalidDeltaLink, "delta_resync_required"));
        var runner = new AirbnbEmailDeltaSyncRunner(
            connectionRepository, syncStateRepository, receiptRepository, authenticator, messageSource,
            new FakeAirbnbReservationDryRunEvaluator(), new FakeAirbnbResolvedReservationSyncPublisher(), new PassThroughExternalIntegrationsTransactionExecutor(), TimeProvider.System,
            NullLogger<AirbnbEmailDeltaSyncRunner>.Instance);

        await runner.RunAsync(TenantId, MailFolderId, CancellationToken.None);

        syncStateRepository.Current!.DeltaLink.Should().BeNull("an invalid/expired deltaLink must be discarded, never silently reused");
        syncStateRepository.Current.LastErrorCode.Should().BeNull("Reset clears the error code too - this is a controlled resync, not a failure to surface");
    }

    [Fact]
    public async Task A_supported_airbnb_message_with_resolved_mapping_is_marked_processed_with_the_parsed_code()
    {
        var connection = ConnectedConnection();
        var connectionRepository = FakeAirbnbEmailMailboxConnectionRepository.WithExisting(connection);
        var syncStateRepository = new FakeAirbnbEmailSyncStateRepository();
        var receiptRepository = new FakeAirbnbEmailMessageReceiptRepository();
        var authenticator = FakeAirbnbEmailAuthenticator.Succeeding(connectionRepository);
        var message = new AirbnbEmailMessageSummary("msg-1", null, DateTimeOffset.UtcNow, "Lembrete de reserva", "automated@airbnb.com", "automated@airbnb.com", "preview");
        var messageSource = new FakeAirbnbEmailMessageSource(
            AirbnbEmailDeltaFetchOutcome.Success(new AirbnbEmailDeltaPage([message], null, "https://graph.microsoft.com/v1.0/delta-1")));
        messageSource.BodiesByMessageId["msg-1"] = "<html>fake body</html>";
        var propertyId = Guid.NewGuid();
        var evaluator = new FakeAirbnbReservationDryRunEvaluator(ReadyOutcome(propertyId));
        var resolvedPublisher = new FakeAirbnbResolvedReservationSyncPublisher();
        var runner = new AirbnbEmailDeltaSyncRunner(
            connectionRepository, syncStateRepository, receiptRepository, authenticator, messageSource,
            evaluator, resolvedPublisher, new PassThroughExternalIntegrationsTransactionExecutor(), TimeProvider.System, NullLogger<AirbnbEmailDeltaSyncRunner>.Instance);

        await runner.RunAsync(TenantId, MailFolderId, CancellationToken.None);

        messageSource.BodyFetchCalls.Should().ContainSingle().Which.Should().Be("msg-1");
        evaluator.Calls.Should().ContainSingle();
        var receipt = receiptRepository.Added.Should().ContainSingle().Subject;
        receipt.ProcessingStatus.Should().Be(AirbnbEmailMessageProcessingStatus.Processed);
        receipt.ExternalReservationId.Should().Be("TESTCODE12");
        resolvedPublisher.Calls.Should().BeEmpty("AutoPublishEnabled defaults to false for every tenant - this must stay pure DRY_RUN evidence, never a real publish");
    }

    [Fact]
    public async Task A_parsed_message_with_no_listing_mapping_is_marked_needs_review_not_failed()
    {
        var connection = ConnectedConnection();
        var connectionRepository = FakeAirbnbEmailMailboxConnectionRepository.WithExisting(connection);
        var syncStateRepository = new FakeAirbnbEmailSyncStateRepository();
        var receiptRepository = new FakeAirbnbEmailMessageReceiptRepository();
        var authenticator = FakeAirbnbEmailAuthenticator.Succeeding(connectionRepository);
        var message = new AirbnbEmailMessageSummary("msg-1", null, DateTimeOffset.UtcNow, "Lembrete de reserva", "automated@airbnb.com", "automated@airbnb.com", "preview");
        var messageSource = new FakeAirbnbEmailMessageSource(
            AirbnbEmailDeltaFetchOutcome.Success(new AirbnbEmailDeltaPage([message], null, "https://graph.microsoft.com/v1.0/delta-1")));
        messageSource.BodiesByMessageId["msg-1"] = "<html>fake body</html>";
        var evaluator = new FakeAirbnbReservationDryRunEvaluator(AirbnbReservationDryRunOutcome.PropertyNotResolved("TESTCODE12"));
        var runner = new AirbnbEmailDeltaSyncRunner(
            connectionRepository, syncStateRepository, receiptRepository, authenticator, messageSource,
            evaluator, new FakeAirbnbResolvedReservationSyncPublisher(), new PassThroughExternalIntegrationsTransactionExecutor(), TimeProvider.System, NullLogger<AirbnbEmailDeltaSyncRunner>.Instance);

        await runner.RunAsync(TenantId, MailFolderId, CancellationToken.None);

        var receipt = receiptRepository.Added.Should().ContainSingle().Subject;
        receipt.ProcessingStatus.Should().Be(AirbnbEmailMessageProcessingStatus.NeedsReview,
            "the email itself parsed fine - a human just needs to add the listing-title mapping, this is not an error in the email");
    }

    [Fact]
    public async Task An_unsupported_template_message_is_marked_failed_with_the_safe_reason()
    {
        var connection = ConnectedConnection();
        var connectionRepository = FakeAirbnbEmailMailboxConnectionRepository.WithExisting(connection);
        var syncStateRepository = new FakeAirbnbEmailSyncStateRepository();
        var receiptRepository = new FakeAirbnbEmailMessageReceiptRepository();
        var authenticator = FakeAirbnbEmailAuthenticator.Succeeding(connectionRepository);
        var message = new AirbnbEmailMessageSummary("msg-1", null, DateTimeOffset.UtcNow, "Some other Airbnb email", "automated@airbnb.com", "automated@airbnb.com", "preview");
        var messageSource = new FakeAirbnbEmailMessageSource(
            AirbnbEmailDeltaFetchOutcome.Success(new AirbnbEmailDeltaPage([message], null, "https://graph.microsoft.com/v1.0/delta-1")));
        messageSource.BodiesByMessageId["msg-1"] = "<html>fake body</html>";
        var evaluator = new FakeAirbnbReservationDryRunEvaluator(
            AirbnbReservationDryRunOutcome.ParseFailed(AirbnbReservationReminderParseFailureReason.UnsupportedTemplate));
        var runner = new AirbnbEmailDeltaSyncRunner(
            connectionRepository, syncStateRepository, receiptRepository, authenticator, messageSource,
            evaluator, new FakeAirbnbResolvedReservationSyncPublisher(), new PassThroughExternalIntegrationsTransactionExecutor(), TimeProvider.System, NullLogger<AirbnbEmailDeltaSyncRunner>.Instance);

        await runner.RunAsync(TenantId, MailFolderId, CancellationToken.None);

        var receipt = receiptRepository.Added.Should().ContainSingle().Subject;
        receipt.ProcessingStatus.Should().Be(AirbnbEmailMessageProcessingStatus.Failed);
        receipt.FailureReason.Should().Be("UnsupportedTemplate");
    }

    [Fact]
    public async Task A_non_airbnb_sender_never_triggers_a_body_fetch_and_the_receipt_stays_pending()
    {
        var connection = ConnectedConnection();
        var connectionRepository = FakeAirbnbEmailMailboxConnectionRepository.WithExisting(connection);
        var syncStateRepository = new FakeAirbnbEmailSyncStateRepository();
        var receiptRepository = new FakeAirbnbEmailMessageReceiptRepository();
        var authenticator = FakeAirbnbEmailAuthenticator.Succeeding(connectionRepository);
        var message = new AirbnbEmailMessageSummary("msg-1", null, DateTimeOffset.UtcNow, "Just a personal email", "someone@example.com", "someone@example.com", "preview");
        var messageSource = new FakeAirbnbEmailMessageSource(
            AirbnbEmailDeltaFetchOutcome.Success(new AirbnbEmailDeltaPage([message], null, "https://graph.microsoft.com/v1.0/delta-1")));
        var evaluator = new FakeAirbnbReservationDryRunEvaluator();
        var runner = new AirbnbEmailDeltaSyncRunner(
            connectionRepository, syncStateRepository, receiptRepository, authenticator, messageSource,
            evaluator, new FakeAirbnbResolvedReservationSyncPublisher(), new PassThroughExternalIntegrationsTransactionExecutor(), TimeProvider.System, NullLogger<AirbnbEmailDeltaSyncRunner>.Instance);

        await runner.RunAsync(TenantId, MailFolderId, CancellationToken.None);

        messageSource.BodyFetchCalls.Should().BeEmpty("only Airbnb-domain senders are ever candidates for a full-body fetch");
        evaluator.Calls.Should().BeEmpty();
        var receipt = receiptRepository.Added.Should().ContainSingle().Subject;
        receipt.ProcessingStatus.Should().Be(AirbnbEmailMessageProcessingStatus.Pending);
    }

    [Fact]
    public async Task Polling_receipts_every_message_and_advances_the_cursor_even_when_one_message_fails_to_parse()
    {
        var connection = ConnectedConnection();
        var connectionRepository = FakeAirbnbEmailMailboxConnectionRepository.WithExisting(connection);
        var syncStateRepository = new FakeAirbnbEmailSyncStateRepository();
        var receiptRepository = new FakeAirbnbEmailMessageReceiptRepository();
        var authenticator = FakeAirbnbEmailAuthenticator.Succeeding(connectionRepository);
        var messages = new[]
        {
            new AirbnbEmailMessageSummary("msg-1", null, DateTimeOffset.UtcNow, "Unsupported Airbnb email", "automated@airbnb.com", "automated@airbnb.com", "preview"),
            new AirbnbEmailMessageSummary("msg-2", null, DateTimeOffset.UtcNow, "Personal email", "someone@example.com", "someone@example.com", "preview"),
        };
        var messageSource = new FakeAirbnbEmailMessageSource(
            AirbnbEmailDeltaFetchOutcome.Success(new AirbnbEmailDeltaPage(messages, null, "https://graph.microsoft.com/v1.0/delta-1")));
        messageSource.BodiesByMessageId["msg-1"] = "<html>fake body</html>";
        var evaluator = new FakeAirbnbReservationDryRunEvaluator(
            AirbnbReservationDryRunOutcome.ParseFailed(AirbnbReservationReminderParseFailureReason.UnsupportedTemplate));
        var runner = new AirbnbEmailDeltaSyncRunner(
            connectionRepository, syncStateRepository, receiptRepository, authenticator, messageSource,
            evaluator, new FakeAirbnbResolvedReservationSyncPublisher(), new PassThroughExternalIntegrationsTransactionExecutor(), TimeProvider.System, NullLogger<AirbnbEmailDeltaSyncRunner>.Instance);

        await runner.RunAsync(TenantId, MailFolderId, CancellationToken.None);

        receiptRepository.Added.Should().HaveCount(2, "one message's parse outcome never blocks the others from being receipted");
        syncStateRepository.Current!.DeltaLink.Should().Be("https://graph.microsoft.com/v1.0/delta-1",
            "an UnsupportedTemplate/ParseFailed outcome is a normal, expected receipt state - not a delta-fetch failure, so the cursor still advances");
    }

    // ==================== Automatic Publication Design + Safety gate ====================

    [Fact]
    public async Task A_would_import_message_received_before_the_cutoff_is_marked_Ignored_and_never_published()
    {
        var cutoff = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
        var connection = ConnectedConnectionWithAutoPublish(cutoff);
        var message = new AirbnbEmailMessageSummary(
            "msg-1", null, cutoff.AddDays(-1), "Lembrete de reserva", "automated@airbnb.com", "automated@airbnb.com", "preview");
        var resolvedPublisher = new FakeAirbnbResolvedReservationSyncPublisher();
        var harness = BuildHarness(
            connection, new FakeAirbnbReservationDryRunEvaluator(ReadyOutcome(Guid.NewGuid())), resolvedPublisher,
            AirbnbEmailDeltaFetchOutcome.Success(new AirbnbEmailDeltaPage([message], null, "https://graph.microsoft.com/v1.0/delta-1")));
        harness.MessageSource.BodiesByMessageId["msg-1"] = "<html>fake body</html>";

        await harness.Runner.RunAsync(TenantId, MailFolderId, CancellationToken.None);

        var receipt = harness.ReceiptRepository.Added.Should().ContainSingle().Subject;
        receipt.ProcessingStatus.Should().Be(AirbnbEmailMessageProcessingStatus.Ignored,
            "the message predates the tenant's own activation cutoff - historical-import protection, never a bulk publish");
        resolvedPublisher.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task A_would_import_message_at_or_after_the_cutoff_is_published_and_marked_Processed()
    {
        var cutoff = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
        var connection = ConnectedConnectionWithAutoPublish(cutoff);
        var propertyId = Guid.NewGuid();
        var message = new AirbnbEmailMessageSummary(
            "msg-1", null, cutoff, "Lembrete de reserva", "automated@airbnb.com", "automated@airbnb.com", "preview");
        var resolvedPublisher = new FakeAirbnbResolvedReservationSyncPublisher();
        var harness = BuildHarness(
            connection, new FakeAirbnbReservationDryRunEvaluator(ReadyOutcome(propertyId)), resolvedPublisher,
            AirbnbEmailDeltaFetchOutcome.Success(new AirbnbEmailDeltaPage([message], null, "https://graph.microsoft.com/v1.0/delta-1")));
        harness.MessageSource.BodiesByMessageId["msg-1"] = "<html>fake body</html>";

        await harness.Runner.RunAsync(TenantId, MailFolderId, CancellationToken.None);

        var call = resolvedPublisher.Calls.Should().ContainSingle().Subject;
        call.PropertyId.Should().Be(propertyId);
        call.ExternalReservationId.Should().Be("TESTCODE12");
        call.GuestCount.Should().Be(2);
        var receipt = harness.ReceiptRepository.Added.Should().ContainSingle().Subject;
        receipt.ProcessingStatus.Should().Be(AirbnbEmailMessageProcessingStatus.Processed);
    }

    [Fact]
    public async Task A_publisher_failure_marks_only_that_receipt_Failed_and_never_blocks_other_messages_in_the_page()
    {
        var cutoff = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
        var connection = ConnectedConnectionWithAutoPublish(cutoff);
        var messages = new[]
        {
            new AirbnbEmailMessageSummary("msg-1", null, cutoff, "Lembrete de reserva", "automated@airbnb.com", "automated@airbnb.com", "preview"),
            new AirbnbEmailMessageSummary("msg-2", null, cutoff, "Personal email", "someone@example.com", "someone@example.com", "preview"),
        };
        var resolvedPublisher = new FakeAirbnbResolvedReservationSyncPublisher(new InvalidOperationException("simulated outbox failure"));
        var harness = BuildHarness(
            connection, new FakeAirbnbReservationDryRunEvaluator(ReadyOutcome(Guid.NewGuid())), resolvedPublisher,
            AirbnbEmailDeltaFetchOutcome.Success(new AirbnbEmailDeltaPage(messages, null, "https://graph.microsoft.com/v1.0/delta-1")));
        harness.MessageSource.BodiesByMessageId["msg-1"] = "<html>fake body</html>";

        await harness.Runner.RunAsync(TenantId, MailFolderId, CancellationToken.None);

        harness.ReceiptRepository.Added.Should().HaveCount(2, "a publish failure for one message must never abort the whole page's transaction");
        var failedReceipt = harness.ReceiptRepository.Added.Single(r => r.GraphMessageId == "msg-1");
        failedReceipt.ProcessingStatus.Should().Be(AirbnbEmailMessageProcessingStatus.Failed);
        failedReceipt.FailureReason.Should().Be("PublisherFailure", "never the raw exception message - a bounded, safe diagnostic code only");
        harness.SyncStateRepository.Current!.DeltaLink.Should().Be("https://graph.microsoft.com/v1.0/delta-1",
            "the cursor still advances - a publisher failure is a normal, captured receipt outcome, not a delta-fetch failure");
    }
}
