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
}
