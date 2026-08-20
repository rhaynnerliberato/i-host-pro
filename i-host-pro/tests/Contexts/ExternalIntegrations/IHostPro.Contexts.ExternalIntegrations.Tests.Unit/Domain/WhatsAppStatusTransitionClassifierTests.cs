using FluentAssertions;
using IHostPro.Contexts.ExternalIntegrations.Domain;

namespace IHostPro.Contexts.ExternalIntegrations.Tests.Unit.Domain;

/// <summary>
/// Fase 9, Checkpoint 2.3.2 mandate §45 (deterministic coverage of the
/// idempotency/monotonicity foundation — ordering, duplicate, regression,
/// the documented Sent→Read skip-Delivered case), corrected in Checkpoint
/// 2.3.2.1 mandate §6/§10 to cover the full approved transition matrix:
/// <c>Sent/Delivered → Failed</c> = Forward, <c>Read → Failed</c> =
/// Regression (Read is terminal for Failed purposes; Delivered is not —
/// see <see cref="WhatsAppStatusTransitionClassifier"/>'s own remarks).
/// </summary>
public class WhatsAppStatusTransitionClassifierTests
{
    [Fact]
    public void No_previous_status_is_always_Forward()
    {
        WhatsAppStatusTransitionClassifier.Classify(null, ProviderMessageStatus.Sent)
            .Should().Be(StatusTransitionClassification.Forward);
    }

    [Theory]
    [InlineData(ProviderMessageStatus.Sent)]
    [InlineData(ProviderMessageStatus.Delivered)]
    [InlineData(ProviderMessageStatus.Read)]
    [InlineData(ProviderMessageStatus.Failed)]
    public void The_same_status_observed_again_is_Duplicate(ProviderMessageStatus status)
    {
        WhatsAppStatusTransitionClassifier.Classify(status, status)
            .Should().Be(StatusTransitionClassification.Duplicate);
    }

    [Fact]
    public void Sent_to_Delivered_is_Forward()
    {
        WhatsAppStatusTransitionClassifier.Classify(ProviderMessageStatus.Sent, ProviderMessageStatus.Delivered)
            .Should().Be(StatusTransitionClassification.Forward);
    }

    [Fact]
    public void Delivered_to_Read_is_Forward()
    {
        WhatsAppStatusTransitionClassifier.Classify(ProviderMessageStatus.Delivered, ProviderMessageStatus.Read)
            .Should().Be(StatusTransitionClassification.Forward);
    }

    [Fact]
    public void Sent_to_Read_directly_is_Forward_without_requiring_Delivered()
    {
        // Meta's own documented behavior: "delivered" can be omitted
        // entirely when "read" arrives almost simultaneously.
        WhatsAppStatusTransitionClassifier.Classify(ProviderMessageStatus.Sent, ProviderMessageStatus.Read)
            .Should().Be(StatusTransitionClassification.Forward);
    }

    [Fact]
    public void Read_to_Delivered_is_Regression()
    {
        WhatsAppStatusTransitionClassifier.Classify(ProviderMessageStatus.Read, ProviderMessageStatus.Delivered)
            .Should().Be(StatusTransitionClassification.Regression);
    }

    [Fact]
    public void Read_to_Sent_is_Regression()
    {
        WhatsAppStatusTransitionClassifier.Classify(ProviderMessageStatus.Read, ProviderMessageStatus.Sent)
            .Should().Be(StatusTransitionClassification.Regression);
    }

    [Fact]
    public void Delivered_to_Sent_is_Regression()
    {
        WhatsAppStatusTransitionClassifier.Classify(ProviderMessageStatus.Delivered, ProviderMessageStatus.Sent)
            .Should().Be(StatusTransitionClassification.Regression);
    }

    [Fact]
    public void Sent_to_Failed_is_Forward()
    {
        WhatsAppStatusTransitionClassifier.Classify(ProviderMessageStatus.Sent, ProviderMessageStatus.Failed)
            .Should().Be(StatusTransitionClassification.Forward);
    }

    [Fact]
    public void Delivered_to_Failed_is_Forward()
    {
        // Checkpoint 2.3.2.1 correction: Delivered is NOT terminal — a later
        // "failed" report is genuine new information, not noise to discard.
        WhatsAppStatusTransitionClassifier.Classify(ProviderMessageStatus.Delivered, ProviderMessageStatus.Failed)
            .Should().Be(StatusTransitionClassification.Forward);
    }

    [Fact]
    public void Read_to_Failed_is_Regression()
    {
        // Read IS treated as terminal for Failed purposes — a "failed"
        // report arriving after "read" is a late/regressive callback.
        WhatsAppStatusTransitionClassifier.Classify(ProviderMessageStatus.Read, ProviderMessageStatus.Failed)
            .Should().Be(StatusTransitionClassification.Regression);
    }

    [Theory]
    [InlineData(ProviderMessageStatus.Sent)]
    [InlineData(ProviderMessageStatus.Delivered)]
    [InlineData(ProviderMessageStatus.Read)]
    public void Nothing_advances_past_Failed(ProviderMessageStatus incoming)
    {
        WhatsAppStatusTransitionClassifier.Classify(ProviderMessageStatus.Failed, incoming)
            .Should().Be(StatusTransitionClassification.Regression, "Failed is terminal — it never transitions forward again");
    }
}
