using FluentAssertions;
using IHostPro.Contexts.ExternalIntegrations.Domain;

namespace IHostPro.Contexts.ExternalIntegrations.Tests.Unit.Domain;

public class AirbnbEmailMessageReceiptTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Create_starts_pending()
    {
        var receipt = AirbnbEmailMessageReceipt.Create(
            Guid.NewGuid(), TenantId, "graph-message-1", "internet-message-1", Now, Now);

        receipt.ProcessingStatus.Should().Be(AirbnbEmailMessageProcessingStatus.Pending);
        receipt.GraphMessageId.Should().Be("graph-message-1");
    }

    [Fact]
    public void Create_rejects_an_empty_graph_message_id()
    {
        var act = () => AirbnbEmailMessageReceipt.Create(Guid.NewGuid(), TenantId, " ", null, Now, Now);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void MarkProcessed_records_the_extracted_reservation_reference()
    {
        var receipt = AirbnbEmailMessageReceipt.Create(Guid.NewGuid(), TenantId, "graph-message-1", null, Now, Now);

        receipt.MarkProcessed("NEW_RESERVATION", "HMABCDEF12", "parser-v1", Now.AddSeconds(1));

        receipt.ProcessingStatus.Should().Be(AirbnbEmailMessageProcessingStatus.Processed);
        receipt.DetectedEventType.Should().Be("NEW_RESERVATION");
        receipt.ExternalReservationId.Should().Be("HMABCDEF12");
        receipt.ParserVersion.Should().Be("parser-v1");
    }

    [Fact]
    public void MarkNeedsReview_never_sets_an_external_reservation_id()
    {
        var receipt = AirbnbEmailMessageReceipt.Create(Guid.NewGuid(), TenantId, "graph-message-1", null, Now, Now);

        receipt.MarkNeedsReview("UNKNOWN_TEMPLATE", "parser-v1", Now.AddSeconds(1));

        receipt.ProcessingStatus.Should().Be(AirbnbEmailMessageProcessingStatus.NeedsReview);
        receipt.ExternalReservationId.Should().BeNull("low-confidence parsing must never resolve to a reservation identifier");
    }

    [Fact]
    public void MarkFailed_requires_a_non_empty_failure_reason()
    {
        var receipt = AirbnbEmailMessageReceipt.Create(Guid.NewGuid(), TenantId, "graph-message-1", null, Now, Now);

        var act = () => receipt.MarkFailed("", "parser-v1", Now);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void MarkFailed_records_the_sanitized_reason()
    {
        var receipt = AirbnbEmailMessageReceipt.Create(Guid.NewGuid(), TenantId, "graph-message-1", null, Now, Now);

        receipt.MarkFailed("PARSER_EXCEPTION", "parser-v1", Now.AddSeconds(1));

        receipt.ProcessingStatus.Should().Be(AirbnbEmailMessageProcessingStatus.Failed);
        receipt.FailureReason.Should().Be("PARSER_EXCEPTION");
    }
}
