using FluentAssertions;
using IHostPro.Contexts.ExternalIntegrations.Application.AirbnbEmailBridge;
using IHostPro.Contexts.ExternalIntegrations.Application.AirbnbReservationParser;
using IHostPro.Contexts.ExternalIntegrations.Domain;
using IHostPro.Contexts.ExternalIntegrations.Tests.Unit.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace IHostPro.Contexts.ExternalIntegrations.Tests.Unit.Application.AirbnbEmailBridge;

public class RetryAirbnbEmailMessageReceiptCommandHandlerTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 9, 0, 0, TimeSpan.Zero);

    private static AirbnbEmailMessageReceipt NewReceipt(DateTimeOffset receivedAtUtc) =>
        AirbnbEmailMessageReceipt.Create(Guid.NewGuid(), TenantId, "graph-message-1", null, receivedAtUtc, receivedAtUtc);

    private static RetryAirbnbEmailMessageReceiptCommandHandler BuildHandler(
        FakeAirbnbEmailMessageReceiptRepository receiptRepository,
        FakeAirbnbEmailMailboxConnectionRepository connectionRepository,
        FakeAirbnbEmailAuthenticator authenticator,
        FakeAirbnbEmailMessageSource messageSource,
        FakeAirbnbReservationDryRunEvaluator evaluator,
        FakeAirbnbResolvedReservationSyncPublisher publisher) =>
        new(
            receiptRepository, connectionRepository, authenticator, messageSource, evaluator, publisher,
            new PassThroughExternalIntegrationsTransactionExecutor(), new PassThroughRetryAirbnbEmailMessageReceiptExecutor(),
            TimeProvider.System, NullLogger<RetryAirbnbEmailMessageReceiptCommandHandler>.Instance);

    [Fact]
    public async Task Handle_returns_ReceiptNotFound_when_the_receipt_does_not_exist()
    {
        var receiptRepository = new FakeAirbnbEmailMessageReceiptRepository();
        var handler = BuildHandler(
            receiptRepository, FakeAirbnbEmailMailboxConnectionRepository.WithExisting(null),
            FakeAirbnbEmailAuthenticator.Succeeding(FakeAirbnbEmailMailboxConnectionRepository.WithExisting(null)),
            new FakeAirbnbEmailMessageSource(), new FakeAirbnbReservationDryRunEvaluator(), new FakeAirbnbResolvedReservationSyncPublisher());

        var result = await handler.Handle(new RetryAirbnbEmailMessageReceiptCommand(TenantId, Guid.NewGuid(), Guid.NewGuid()), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Code.Should().Be(AirbnbEmailBridgeErrorCodes.ReceiptNotFound);
    }

    [Fact]
    public async Task Handle_returns_ReceiptNotFound_when_the_receipt_belongs_to_another_tenant()
    {
        var receipt = AirbnbEmailMessageReceipt.Create(Guid.NewGuid(), Guid.NewGuid(), "graph-message-1", null, Now, Now);
        receipt.MarkNeedsReview("RESERVATION_REMINDER", "parser-v1", Now, "Studio Sem Mapeamento");
        var receiptRepository = new FakeAirbnbEmailMessageReceiptRepository();
        receiptRepository.Add(receipt);

        var handler = BuildHandler(
            receiptRepository, FakeAirbnbEmailMailboxConnectionRepository.WithExisting(null),
            FakeAirbnbEmailAuthenticator.Succeeding(FakeAirbnbEmailMailboxConnectionRepository.WithExisting(null)),
            new FakeAirbnbEmailMessageSource(), new FakeAirbnbReservationDryRunEvaluator(), new FakeAirbnbResolvedReservationSyncPublisher());

        var result = await handler.Handle(new RetryAirbnbEmailMessageReceiptCommand(TenantId, receipt.Id, Guid.NewGuid()), CancellationToken.None);

        result.Error.Code.Should().Be(AirbnbEmailBridgeErrorCodes.ReceiptNotFound);
    }

    [Theory]
    [InlineData(AirbnbEmailMessageProcessingStatus.Processed)]
    [InlineData(AirbnbEmailMessageProcessingStatus.Ignored)]
    [InlineData(AirbnbEmailMessageProcessingStatus.Pending)]
    public async Task Handle_rejects_terminal_or_non_terminal_statuses_that_are_never_retryable(AirbnbEmailMessageProcessingStatus status)
    {
        var receipt = NewReceipt(Now);
        switch (status)
        {
            case AirbnbEmailMessageProcessingStatus.Processed:
                receipt.MarkProcessed("RESERVATION_REMINDER", "HMABCDEF12", "parser-v1", Now);
                break;
            case AirbnbEmailMessageProcessingStatus.Ignored:
                receipt.MarkIgnored("RESERVATION_REMINDER", "parser-v1", Now);
                break;
        }

        var receiptRepository = new FakeAirbnbEmailMessageReceiptRepository();
        receiptRepository.Add(receipt);

        var handler = BuildHandler(
            receiptRepository, FakeAirbnbEmailMailboxConnectionRepository.WithExisting(null),
            FakeAirbnbEmailAuthenticator.Succeeding(FakeAirbnbEmailMailboxConnectionRepository.WithExisting(null)),
            new FakeAirbnbEmailMessageSource(), new FakeAirbnbReservationDryRunEvaluator(), new FakeAirbnbResolvedReservationSyncPublisher());

        var result = await handler.Handle(new RetryAirbnbEmailMessageReceiptCommand(TenantId, receipt.Id, Guid.NewGuid()), CancellationToken.None);

        result.Error.Code.Should().Be(AirbnbEmailBridgeErrorCodes.ReceiptNotRetryable);
    }

    [Fact]
    public async Task Handle_rejects_a_Failed_receipt_whose_reason_is_not_PublisherFailure()
    {
        var receipt = NewReceipt(Now);
        receipt.MarkFailed(AirbnbReservationReminderParseFailureReason.MissingRequiredField.ToString(), "parser-v1", Now);
        var receiptRepository = new FakeAirbnbEmailMessageReceiptRepository();
        receiptRepository.Add(receipt);

        var handler = BuildHandler(
            receiptRepository, FakeAirbnbEmailMailboxConnectionRepository.WithExisting(null),
            FakeAirbnbEmailAuthenticator.Succeeding(FakeAirbnbEmailMailboxConnectionRepository.WithExisting(null)),
            new FakeAirbnbEmailMessageSource(), new FakeAirbnbReservationDryRunEvaluator(), new FakeAirbnbResolvedReservationSyncPublisher());

        var result = await handler.Handle(new RetryAirbnbEmailMessageReceiptCommand(TenantId, receipt.Id, Guid.NewGuid()), CancellationToken.None);

        result.Error.Code.Should().Be(AirbnbEmailBridgeErrorCodes.ReceiptNotRetryable);
    }

    [Fact]
    public async Task Handle_returns_MailboxNotConnected_when_silent_auth_fails_and_never_touches_the_receipt()
    {
        var receipt = NewReceipt(Now);
        receipt.MarkNeedsReview("RESERVATION_REMINDER", "parser-v1", Now, "Studio Sem Mapeamento");
        var receiptRepository = new FakeAirbnbEmailMessageReceiptRepository();
        receiptRepository.Add(receipt);
        var connectionRepository = FakeAirbnbEmailMailboxConnectionRepository.WithExisting(null);
        var authenticator = FakeAirbnbEmailAuthenticator.Succeeding(connectionRepository);
        authenticator.SilentOutcome = AirbnbEmailSilentAcquisitionOutcome.Failure(reauthorizationRequired: true);

        var handler = BuildHandler(
            receiptRepository, connectionRepository, authenticator,
            new FakeAirbnbEmailMessageSource(), new FakeAirbnbReservationDryRunEvaluator(), new FakeAirbnbResolvedReservationSyncPublisher());

        var result = await handler.Handle(new RetryAirbnbEmailMessageReceiptCommand(TenantId, receipt.Id, Guid.NewGuid()), CancellationToken.None);

        result.Error.Code.Should().Be(AirbnbEmailBridgeErrorCodes.ReceiptMailboxNotConnected);
        receipt.ProcessingStatus.Should().Be(AirbnbEmailMessageProcessingStatus.NeedsReview, "a failed retry attempt must never mutate the receipt");
    }

    [Fact]
    public async Task Handle_returns_SourceMessageUnavailable_when_Graph_no_longer_has_the_message()
    {
        var receipt = NewReceipt(Now);
        receipt.MarkNeedsReview("RESERVATION_REMINDER", "parser-v1", Now, "Studio Sem Mapeamento");
        var receiptRepository = new FakeAirbnbEmailMessageReceiptRepository();
        receiptRepository.Add(receipt);
        var connectionRepository = FakeAirbnbEmailMailboxConnectionRepository.WithExisting(null);
        var messageSource = new FakeAirbnbEmailMessageSource(); // no body registered for "graph-message-1" => null content

        var handler = BuildHandler(
            receiptRepository, connectionRepository, FakeAirbnbEmailAuthenticator.Succeeding(connectionRepository),
            messageSource, new FakeAirbnbReservationDryRunEvaluator(), new FakeAirbnbResolvedReservationSyncPublisher());

        var result = await handler.Handle(new RetryAirbnbEmailMessageReceiptCommand(TenantId, receipt.Id, Guid.NewGuid()), CancellationToken.None);

        result.Error.Code.Should().Be(AirbnbEmailBridgeErrorCodes.ReceiptSourceMessageUnavailable);
        receipt.ProcessingStatus.Should().Be(AirbnbEmailMessageProcessingStatus.NeedsReview);
    }

    [Fact]
    public async Task Handle_marks_NeedsReview_Processed_once_the_mapping_now_resolves_and_auto_publish_is_disabled()
    {
        var receipt = NewReceipt(Now);
        receipt.MarkNeedsReview("RESERVATION_REMINDER", "parser-v1", Now, "Studio Sem Mapeamento");
        var receiptRepository = new FakeAirbnbEmailMessageReceiptRepository();
        receiptRepository.Add(receipt);
        var connectionRepository = FakeAirbnbEmailMailboxConnectionRepository.WithExisting(null);
        var messageSource = new FakeAirbnbEmailMessageSource();
        messageSource.BodiesByMessageId["graph-message-1"] = "<html>fake body</html>";
        var propertyId = Guid.NewGuid();
        var outcome = AirbnbReservationDryRunOutcome.Ready(propertyId, "HMABCDEF12", "Hospede Teste", Now.AddDays(2), Now.AddDays(5), 2);
        var publisher = new FakeAirbnbResolvedReservationSyncPublisher();

        var handler = BuildHandler(
            receiptRepository, connectionRepository, FakeAirbnbEmailAuthenticator.Succeeding(connectionRepository),
            messageSource, new FakeAirbnbReservationDryRunEvaluator(outcome), publisher);

        var result = await handler.Handle(new RetryAirbnbEmailMessageReceiptCommand(TenantId, receipt.Id, Guid.NewGuid()), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.ProcessingStatus.Should().Be(nameof(AirbnbEmailMessageProcessingStatus.Processed));
        receipt.ProcessingStatus.Should().Be(AirbnbEmailMessageProcessingStatus.Processed);
        receipt.UnmatchedListingTitle.Should().BeNull("resolving the mapping must clear the stale unmatched listing title");
        publisher.Calls.Should().BeEmpty("auto-publish is disabled - this must remain DRY_RUN evidence only, exactly like the normal delta-sync path");
        receiptRepository.Added.Should().ContainSingle("retry must reuse the SAME receipt, never insert a new one");
    }

    [Fact]
    public async Task Handle_publishes_once_when_auto_publish_is_enabled_and_the_receipt_is_at_or_after_the_cutoff()
    {
        var receivedAtUtc = Now;
        var receipt = NewReceipt(receivedAtUtc);
        receipt.MarkNeedsReview("RESERVATION_REMINDER", "parser-v1", Now, "Studio Sem Mapeamento");
        var receiptRepository = new FakeAirbnbEmailMessageReceiptRepository();
        receiptRepository.Add(receipt);

        var connection = AirbnbEmailMailboxConnection.Create(Guid.NewGuid(), TenantId, Now);
        connection.Connect("home-account-1", "entra-tenant-1", "guest@hotmail.com", "Mail.Read", Now);
        connection.EnableAutoPublish(notBeforeUtc: receivedAtUtc.AddDays(-1), now: Now);
        var connectionRepository = FakeAirbnbEmailMailboxConnectionRepository.WithExisting(connection);

        var messageSource = new FakeAirbnbEmailMessageSource();
        messageSource.BodiesByMessageId["graph-message-1"] = "<html>fake body</html>";
        var propertyId = Guid.NewGuid();
        var outcome = AirbnbReservationDryRunOutcome.Ready(propertyId, "HMABCDEF12", "Hospede Teste", receivedAtUtc.AddDays(2), receivedAtUtc.AddDays(5), 2);
        var publisher = new FakeAirbnbResolvedReservationSyncPublisher();

        var handler = BuildHandler(
            receiptRepository, connectionRepository, FakeAirbnbEmailAuthenticator.Succeeding(connectionRepository),
            messageSource, new FakeAirbnbReservationDryRunEvaluator(outcome), publisher);

        var result = await handler.Handle(new RetryAirbnbEmailMessageReceiptCommand(TenantId, receipt.Id, Guid.NewGuid()), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        receipt.ProcessingStatus.Should().Be(AirbnbEmailMessageProcessingStatus.Processed);
        publisher.Calls.Should().ContainSingle();
        publisher.Calls[0].PropertyId.Should().Be(propertyId);
    }

    [Fact]
    public async Task Handle_never_publishes_a_receipt_that_predates_the_auto_publish_cutoff_even_when_retried_long_after_activation()
    {
        var receivedAtUtc = Now; // the ORIGINAL message timestamp - deliberately predates the cutoff below
        var receipt = NewReceipt(receivedAtUtc);
        receipt.MarkNeedsReview("RESERVATION_REMINDER", "parser-v1", Now, "Studio Sem Mapeamento");
        var receiptRepository = new FakeAirbnbEmailMessageReceiptRepository();
        receiptRepository.Add(receipt);

        var connection = AirbnbEmailMailboxConnection.Create(Guid.NewGuid(), TenantId, Now);
        connection.Connect("home-account-1", "entra-tenant-1", "guest@hotmail.com", "Mail.Read", Now);
        // Cutoff is AFTER the message's own ReceivedAtUtc - even though this
        // retry itself happens much later ("now"), the historical protection
        // must key off the ORIGINAL receipt timestamp, never retry time.
        connection.EnableAutoPublish(notBeforeUtc: receivedAtUtc.AddDays(1), now: Now);
        var connectionRepository = FakeAirbnbEmailMailboxConnectionRepository.WithExisting(connection);

        var messageSource = new FakeAirbnbEmailMessageSource();
        messageSource.BodiesByMessageId["graph-message-1"] = "<html>fake body</html>";
        var outcome = AirbnbReservationDryRunOutcome.Ready(
            Guid.NewGuid(), "HMABCDEF12", "Hospede Teste", receivedAtUtc.AddDays(2), receivedAtUtc.AddDays(5), 2);
        var publisher = new FakeAirbnbResolvedReservationSyncPublisher();

        var handler = BuildHandler(
            receiptRepository, connectionRepository, FakeAirbnbEmailAuthenticator.Succeeding(connectionRepository),
            messageSource, new FakeAirbnbReservationDryRunEvaluator(outcome), publisher);

        var result = await handler.Handle(new RetryAirbnbEmailMessageReceiptCommand(TenantId, receipt.Id, Guid.NewGuid()), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        receipt.ProcessingStatus.Should().Be(AirbnbEmailMessageProcessingStatus.Ignored);
        publisher.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_marks_Processed_when_retrying_a_PublisherFailure_receipt_and_the_publish_now_succeeds()
    {
        var receipt = NewReceipt(Now);
        receipt.MarkFailed("PublisherFailure", "parser-v1", Now);
        var receiptRepository = new FakeAirbnbEmailMessageReceiptRepository();
        receiptRepository.Add(receipt);

        var connection = AirbnbEmailMailboxConnection.Create(Guid.NewGuid(), TenantId, Now);
        connection.Connect("home-account-1", "entra-tenant-1", "guest@hotmail.com", "Mail.Read", Now);
        connection.EnableAutoPublish(notBeforeUtc: Now.AddDays(-1), now: Now);
        var connectionRepository = FakeAirbnbEmailMailboxConnectionRepository.WithExisting(connection);

        var messageSource = new FakeAirbnbEmailMessageSource();
        messageSource.BodiesByMessageId["graph-message-1"] = "<html>fake body</html>";
        var outcome = AirbnbReservationDryRunOutcome.Ready(
            Guid.NewGuid(), "HMABCDEF12", "Hospede Teste", Now.AddDays(2), Now.AddDays(5), 2);
        var publisher = new FakeAirbnbResolvedReservationSyncPublisher();

        var handler = BuildHandler(
            receiptRepository, connectionRepository, FakeAirbnbEmailAuthenticator.Succeeding(connectionRepository),
            messageSource, new FakeAirbnbReservationDryRunEvaluator(outcome), publisher);

        var result = await handler.Handle(new RetryAirbnbEmailMessageReceiptCommand(TenantId, receipt.Id, Guid.NewGuid()), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        receipt.ProcessingStatus.Should().Be(AirbnbEmailMessageProcessingStatus.Processed);
        publisher.Calls.Should().ContainSingle();
    }

    [Fact]
    public async Task Handle_keeps_the_receipt_Failed_PublisherFailure_when_the_publish_fails_again()
    {
        var receipt = NewReceipt(Now);
        receipt.MarkFailed("PublisherFailure", "parser-v1", Now);
        var receiptRepository = new FakeAirbnbEmailMessageReceiptRepository();
        receiptRepository.Add(receipt);

        var connection = AirbnbEmailMailboxConnection.Create(Guid.NewGuid(), TenantId, Now);
        connection.Connect("home-account-1", "entra-tenant-1", "guest@hotmail.com", "Mail.Read", Now);
        connection.EnableAutoPublish(notBeforeUtc: Now.AddDays(-1), now: Now);
        var connectionRepository = FakeAirbnbEmailMailboxConnectionRepository.WithExisting(connection);

        var messageSource = new FakeAirbnbEmailMessageSource();
        messageSource.BodiesByMessageId["graph-message-1"] = "<html>fake body</html>";
        var outcome = AirbnbReservationDryRunOutcome.Ready(
            Guid.NewGuid(), "HMABCDEF12", "Hospede Teste", Now.AddDays(2), Now.AddDays(5), 2);
        var publisher = new FakeAirbnbResolvedReservationSyncPublisher(new InvalidOperationException("simulated transient publish failure"));

        var handler = BuildHandler(
            receiptRepository, connectionRepository, FakeAirbnbEmailAuthenticator.Succeeding(connectionRepository),
            messageSource, new FakeAirbnbReservationDryRunEvaluator(outcome), publisher);

        var result = await handler.Handle(new RetryAirbnbEmailMessageReceiptCommand(TenantId, receipt.Id, Guid.NewGuid()), CancellationToken.None);

        result.IsSuccess.Should().BeTrue("the retry attempt itself is not an API-level failure - the outcome is recorded on the receipt");
        receipt.ProcessingStatus.Should().Be(AirbnbEmailMessageProcessingStatus.Failed);
        receipt.FailureReason.Should().Be("PublisherFailure");
    }

    [Fact]
    public async Task Handle_marks_NeedsReview_again_with_the_same_unmatched_listing_title_when_the_mapping_still_does_not_resolve()
    {
        var receipt = NewReceipt(Now);
        receipt.MarkNeedsReview("RESERVATION_REMINDER", "parser-v1", Now, "Studio Sem Mapeamento");
        var receiptRepository = new FakeAirbnbEmailMessageReceiptRepository();
        receiptRepository.Add(receipt);
        var connectionRepository = FakeAirbnbEmailMailboxConnectionRepository.WithExisting(null);
        var messageSource = new FakeAirbnbEmailMessageSource();
        messageSource.BodiesByMessageId["graph-message-1"] = "<html>fake body</html>";
        var outcome = AirbnbReservationDryRunOutcome.PropertyNotResolved("HMABCDEF12", "Studio Sem Mapeamento");

        var handler = BuildHandler(
            receiptRepository, connectionRepository, FakeAirbnbEmailAuthenticator.Succeeding(connectionRepository),
            messageSource, new FakeAirbnbReservationDryRunEvaluator(outcome), new FakeAirbnbResolvedReservationSyncPublisher());

        var result = await handler.Handle(new RetryAirbnbEmailMessageReceiptCommand(TenantId, receipt.Id, Guid.NewGuid()), CancellationToken.None);

        result.IsSuccess.Should().BeTrue("a still-unresolved mapping is a legitimate outcome, not a rejected request");
        receipt.ProcessingStatus.Should().Be(AirbnbEmailMessageProcessingStatus.NeedsReview);
        receipt.UnmatchedListingTitle.Should().Be("Studio Sem Mapeamento");
    }
}
