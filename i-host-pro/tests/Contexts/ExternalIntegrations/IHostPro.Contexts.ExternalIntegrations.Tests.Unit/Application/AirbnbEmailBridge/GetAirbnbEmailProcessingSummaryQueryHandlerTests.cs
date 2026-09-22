using FluentAssertions;
using IHostPro.Contexts.ExternalIntegrations.Application.AirbnbEmailBridge;
using IHostPro.Contexts.ExternalIntegrations.Domain;

namespace IHostPro.Contexts.ExternalIntegrations.Tests.Unit.Application.AirbnbEmailBridge;

/// <summary>Airbnb Email Bridge Minimal Operations/UX gate.</summary>
public class GetAirbnbEmailProcessingSummaryQueryHandlerTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);

    private static AirbnbEmailMessageReceipt NewReceipt() =>
        AirbnbEmailMessageReceipt.Create(Guid.NewGuid(), TenantId, Guid.NewGuid().ToString(), null, Now, Now);

    [Fact]
    public async Task Returns_all_zero_counts_for_a_tenant_with_no_receipts()
    {
        var repository = new FakeAirbnbEmailMessageReceiptRepository();
        var handler = new GetAirbnbEmailProcessingSummaryQueryHandler(repository);

        var result = await handler.Handle(new GetAirbnbEmailProcessingSummaryQuery(TenantId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Pending.Should().Be(0);
        result.Value.Processed.Should().Be(0);
        result.Value.NeedsReview.Should().Be(0);
        result.Value.Failed.Should().Be(0);
        result.Value.Ignored.Should().Be(0);
    }

    [Fact]
    public async Task Counts_each_status_correctly_across_a_mixed_set_of_receipts()
    {
        var repository = new FakeAirbnbEmailMessageReceiptRepository();

        var pending = NewReceipt();
        repository.Add(pending); // stays Pending

        var processed1 = NewReceipt();
        processed1.MarkProcessed("RESERVATION_REMINDER", "HMABCDEF12", "parser-v1", Now);
        repository.Add(processed1);

        var processed2 = NewReceipt();
        processed2.MarkProcessed("RESERVATION_REMINDER", "HMABCDEF34", "parser-v1", Now);
        repository.Add(processed2);

        var needsReview = NewReceipt();
        needsReview.MarkNeedsReview("RESERVATION_REMINDER", "parser-v1", Now, "Studio Sem Mapeamento");
        repository.Add(needsReview);

        var failed = NewReceipt();
        failed.MarkFailed("PARSER_EXCEPTION", "parser-v1", Now);
        repository.Add(failed);

        var ignored = NewReceipt();
        ignored.MarkIgnored("RESERVATION_REMINDER", "parser-v1", Now);
        repository.Add(ignored);

        var handler = new GetAirbnbEmailProcessingSummaryQueryHandler(repository);

        var result = await handler.Handle(new GetAirbnbEmailProcessingSummaryQuery(TenantId), CancellationToken.None);

        result.Value.Pending.Should().Be(1);
        result.Value.Processed.Should().Be(2);
        result.Value.NeedsReview.Should().Be(1);
        result.Value.Failed.Should().Be(1);
        result.Value.Ignored.Should().Be(1);
    }

    [Fact]
    public async Task Never_exposes_guest_or_mailbox_data_in_the_result_shape()
    {
        var props = typeof(AirbnbEmailProcessingSummaryResult).GetProperties().Select(p => p.Name);
        props.Should().BeEquivalentTo(["TenantId", "Pending", "Processed", "NeedsReview", "Failed", "Ignored"]);
    }
}
