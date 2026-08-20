using FluentAssertions;
using IHostPro.Contexts.Communication.Domain;

namespace IHostPro.Contexts.Communication.Tests.Unit.Domain;

public class MessageTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid ReservationId = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 8, 18, 12, 0, 0, TimeSpan.Zero);

    private static Message CreateMessage() => Message.Create(
        Guid.NewGuid(), TenantId, ReservationId, "WhatsApp", "RESERVATION_CONFIRMATION",
        "*******1234", "Olá, sua reserva foi confirmada.", "idempotency-key", Now);

    [Fact]
    public void Create_starts_in_Created_status()
    {
        var message = CreateMessage();

        message.Status.Should().Be(MessageStatus.Created);
        message.CreatedAtUtc.Should().Be(Now);
        message.SentAtUtc.Should().BeNull();
        message.FailedAtUtc.Should().BeNull();
        message.FailureReason.Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_rejects_empty_channel(string channel)
    {
        var act = () => Message.Create(Guid.NewGuid(), TenantId, ReservationId, channel, "KEY", null, "content", "key", Now);

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_rejects_empty_templateKey(string templateKey)
    {
        var act = () => Message.Create(Guid.NewGuid(), TenantId, ReservationId, "WhatsApp", templateKey, null, "content", "key", Now);

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_rejects_empty_renderedContent(string content)
    {
        var act = () => Message.Create(Guid.NewGuid(), TenantId, ReservationId, "WhatsApp", "KEY", null, content, "key", Now);

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_rejects_empty_idempotencyKey(string idempotencyKey)
    {
        var act = () => Message.Create(Guid.NewGuid(), TenantId, ReservationId, "WhatsApp", "KEY", null, "content", idempotencyKey, Now);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void MarkQueued_transitions_Created_to_Queued()
    {
        var message = CreateMessage();

        message.MarkQueued();

        message.Status.Should().Be(MessageStatus.Queued);
    }

    [Fact]
    public void MarkQueued_from_a_non_Created_status_throws()
    {
        var message = CreateMessage();
        message.MarkQueued();

        var act = () => message.MarkQueued();

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void MarkSending_transitions_Queued_to_Sending()
    {
        var message = CreateMessage();
        message.MarkQueued();

        message.MarkSending();

        message.Status.Should().Be(MessageStatus.Sending);
    }

    [Fact]
    public void MarkSending_from_Created_throws()
    {
        var message = CreateMessage();

        var act = () => message.MarkSending();

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void MarkSent_transitions_Sending_to_Sent_and_stamps_SentAtUtc()
    {
        var message = CreateMessage();
        message.MarkQueued();
        message.MarkSending();
        var sentAt = Now.AddSeconds(5);

        message.MarkSent(sentAt);

        message.Status.Should().Be(MessageStatus.Sent);
        message.SentAtUtc.Should().Be(sentAt);
        message.ProviderMessageId.Should().BeNull();
    }

    [Fact]
    public void MarkSent_stores_the_providerMessageId_when_the_connector_reports_one()
    {
        var message = CreateMessage();
        message.MarkQueued();
        message.MarkSending();

        message.MarkSent(Now.AddSeconds(5), "wamid.HBgL...");

        message.Status.Should().Be(MessageStatus.Sent);
        message.ProviderMessageId.Should().Be("wamid.HBgL...");
    }

    [Fact]
    public void MarkSent_from_Queued_throws()
    {
        var message = CreateMessage();
        message.MarkQueued();

        var act = () => message.MarkSent(Now.AddSeconds(1));

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void MarkFailed_from_Queued_succeeds_representing_no_connector_call_attempted()
    {
        var message = CreateMessage();
        message.MarkQueued();
        var failedAt = Now.AddSeconds(1);

        message.MarkFailed("no_contact_available", failedAt);

        message.Status.Should().Be(MessageStatus.Failed);
        message.FailureReason.Should().Be("no_contact_available");
        message.FailedAtUtc.Should().Be(failedAt);
    }

    [Fact]
    public void MarkFailed_from_Sending_succeeds_representing_a_connector_rejection()
    {
        var message = CreateMessage();
        message.MarkQueued();
        message.MarkSending();

        message.MarkFailed("connector_rejected", Now.AddSeconds(1));

        message.Status.Should().Be(MessageStatus.Failed);
        message.FailureReason.Should().Be("connector_rejected");
    }

    [Fact]
    public void MarkFailed_from_Created_throws()
    {
        var message = CreateMessage();

        var act = () => message.MarkFailed("reason", Now.AddSeconds(1));

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void MarkFailed_from_a_terminal_status_throws()
    {
        var message = CreateMessage();
        message.MarkQueued();
        message.MarkSending();
        message.MarkSent(Now.AddSeconds(1));

        var act = () => message.MarkFailed("reason", Now.AddSeconds(2));

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void MarkFailed_rejects_an_empty_reason()
    {
        var message = CreateMessage();
        message.MarkQueued();

        var act = () => message.MarkFailed("", Now.AddSeconds(1));

        act.Should().Throw<ArgumentException>();
    }

    // Fase 9, Checkpoint 2.3.3 (ADR-022 item 14) — ApplyProviderStatus's full
    // approved transition matrix (mandate §6/§17-21), exercised against the
    // real aggregate, not just a standalone classifier (mandate §34).

    private static Message SentMessage()
    {
        var message = CreateMessage();
        message.MarkQueued();
        message.MarkSending();
        message.MarkSent(Now, "wamid.HBgL...");
        return message;
    }

    private static Message DeliveredMessage()
    {
        var message = SentMessage();
        message.ApplyProviderStatus(WhatsAppProviderStatus.Delivered, Now.AddSeconds(1));
        return message;
    }

    private static Message ReadMessage()
    {
        var message = DeliveredMessage();
        message.ApplyProviderStatus(WhatsAppProviderStatus.Read, Now.AddSeconds(2));
        return message;
    }

    private static Message FailedMessage()
    {
        var message = SentMessage();
        message.ApplyProviderStatus(WhatsAppProviderStatus.Failed, Now.AddSeconds(1));
        return message;
    }

    [Theory]
    [InlineData(MessageStatus.Created)]
    [InlineData(MessageStatus.Queued)]
    [InlineData(MessageStatus.Sending)]
    public void ApplyProviderStatus_before_Sent_throws(MessageStatus preSentStatus)
    {
        var message = CreateMessage();
        if (preSentStatus is MessageStatus.Queued or MessageStatus.Sending)
            message.MarkQueued();
        if (preSentStatus == MessageStatus.Sending)
            message.MarkSending();

        var act = () => message.ApplyProviderStatus(WhatsAppProviderStatus.Delivered, Now);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Sent_to_Delivered_is_Applied_and_stamps_DeliveredAtUtc()
    {
        var message = SentMessage();
        var occurredAt = Now.AddSeconds(1);

        var result = message.ApplyProviderStatus(WhatsAppProviderStatus.Delivered, occurredAt);

        result.Should().Be(ProviderStatusApplicationResult.Applied);
        message.Status.Should().Be(MessageStatus.Delivered);
        message.DeliveredAtUtc.Should().Be(occurredAt);
    }

    [Fact]
    public void Sent_to_Read_directly_is_Applied_without_requiring_Delivered()
    {
        var message = SentMessage();
        var occurredAt = Now.AddSeconds(1);

        var result = message.ApplyProviderStatus(WhatsAppProviderStatus.Read, occurredAt);

        result.Should().Be(ProviderStatusApplicationResult.Applied);
        message.Status.Should().Be(MessageStatus.Read);
        message.ReadAtUtc.Should().Be(occurredAt);
    }

    [Fact]
    public void Sent_to_Failed_is_Applied_and_stamps_FailedAtUtc_and_FailureReason()
    {
        var message = SentMessage();
        var occurredAt = Now.AddSeconds(1);

        var result = message.ApplyProviderStatus(WhatsAppProviderStatus.Failed, occurredAt, 131026);

        result.Should().Be(ProviderStatusApplicationResult.Applied);
        message.Status.Should().Be(MessageStatus.Failed);
        message.FailedAtUtc.Should().Be(occurredAt);
        message.FailureReason.Should().Be("provider_error_131026");
    }

    [Fact]
    public void Failed_without_a_provider_error_code_uses_a_generic_reason()
    {
        var message = SentMessage();

        message.ApplyProviderStatus(WhatsAppProviderStatus.Failed, Now.AddSeconds(1));

        message.FailureReason.Should().Be("provider_reported_failure");
    }

    [Fact]
    public void Delivered_to_Read_is_Applied()
    {
        var message = DeliveredMessage();
        var occurredAt = Now.AddSeconds(3);

        var result = message.ApplyProviderStatus(WhatsAppProviderStatus.Read, occurredAt);

        result.Should().Be(ProviderStatusApplicationResult.Applied);
        message.Status.Should().Be(MessageStatus.Read);
        message.ReadAtUtc.Should().Be(occurredAt);
    }

    [Fact]
    public void Delivered_to_Failed_is_Applied()
    {
        // Checkpoint 2.3.2.1 correction, mirrored here: Delivered is NOT
        // terminal — a later "failed" report is genuine new information.
        var message = DeliveredMessage();

        var result = message.ApplyProviderStatus(WhatsAppProviderStatus.Failed, Now.AddSeconds(3));

        result.Should().Be(ProviderStatusApplicationResult.Applied);
        message.Status.Should().Be(MessageStatus.Failed);
    }

    [Fact]
    public void Read_to_Failed_is_Regression_and_does_not_mutate_the_message()
    {
        // Read IS treated as terminal for Failed purposes.
        var message = ReadMessage();
        var readAt = message.ReadAtUtc;

        var result = message.ApplyProviderStatus(WhatsAppProviderStatus.Failed, Now.AddSeconds(10));

        result.Should().Be(ProviderStatusApplicationResult.Regression);
        message.Status.Should().Be(MessageStatus.Read);
        message.ReadAtUtc.Should().Be(readAt);
        message.FailedAtUtc.Should().BeNull();
    }

    [Theory]
    [InlineData(WhatsAppProviderStatus.Sent)]
    [InlineData(WhatsAppProviderStatus.Delivered)]
    [InlineData(WhatsAppProviderStatus.Read)]
    public void Nothing_advances_past_Failed(WhatsAppProviderStatus incoming)
    {
        var message = FailedMessage();

        var result = message.ApplyProviderStatus(incoming, Now.AddSeconds(10));

        result.Should().Be(ProviderStatusApplicationResult.Regression);
        message.Status.Should().Be(MessageStatus.Failed);
    }

    [Fact]
    public void Failed_to_Failed_is_Duplicate()
    {
        var message = FailedMessage();

        var result = message.ApplyProviderStatus(WhatsAppProviderStatus.Failed, Now.AddSeconds(10));

        result.Should().Be(ProviderStatusApplicationResult.Duplicate);
    }

    [Theory]
    [InlineData(WhatsAppProviderStatus.Sent)]
    [InlineData(WhatsAppProviderStatus.Delivered)]
    [InlineData(WhatsAppProviderStatus.Read)]
    public void The_same_status_observed_again_is_Duplicate_and_does_not_mutate(WhatsAppProviderStatus status)
    {
        var message = status switch
        {
            WhatsAppProviderStatus.Sent => SentMessage(),
            WhatsAppProviderStatus.Delivered => DeliveredMessage(),
            WhatsAppProviderStatus.Read => ReadMessage(),
            _ => throw new ArgumentOutOfRangeException(nameof(status)),
        };
        var statusBefore = message.Status;

        var result = message.ApplyProviderStatus(status, Now.AddSeconds(10));

        result.Should().Be(ProviderStatusApplicationResult.Duplicate);
        message.Status.Should().Be(statusBefore);
    }

    [Fact]
    public void Delivered_to_Sent_is_Regression()
    {
        var message = DeliveredMessage();

        var result = message.ApplyProviderStatus(WhatsAppProviderStatus.Sent, Now.AddSeconds(10));

        result.Should().Be(ProviderStatusApplicationResult.Regression);
        message.Status.Should().Be(MessageStatus.Delivered);
    }

    [Fact]
    public void Read_to_Sent_is_Regression()
    {
        var message = ReadMessage();

        var result = message.ApplyProviderStatus(WhatsAppProviderStatus.Sent, Now.AddSeconds(10));

        result.Should().Be(ProviderStatusApplicationResult.Regression);
        message.Status.Should().Be(MessageStatus.Read);
    }

    [Fact]
    public void Read_to_Delivered_is_Regression()
    {
        var message = ReadMessage();

        var result = message.ApplyProviderStatus(WhatsAppProviderStatus.Delivered, Now.AddSeconds(10));

        result.Should().Be(ProviderStatusApplicationResult.Regression);
        message.Status.Should().Be(MessageStatus.Read);
    }
}
