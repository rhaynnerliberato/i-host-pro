using FluentAssertions;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.AIAgent.Application.Tools;
using IHostPro.Contexts.AIAgent.Infrastructure.Tools;
using IHostPro.Contexts.Payments.Application;

namespace IHostPro.Contexts.AIAgent.Tests.Unit.Tools;

public class GetPaymentStatusToolTests
{
    private static readonly AgentToolContext Context = new(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

    [Fact]
    public async Task ExecuteAsync_returns_the_status_verbatim_and_excludes_sensitive_fields()
    {
        var dispatcher = new FakePaymentsRequestDispatcher();
        var status = new PaymentStatusResult("Confirmed", 150.00m, "BRL", null);
        dispatcher.Stub.SetResponse(new GetPaymentStatusByReservationQuery(Context.ReservationId), Result.Success(status));
        var tool = new GetPaymentStatusTool(dispatcher);

        var result = await tool.ExecuteAsync(Context, null, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Content.Should().Contain("Confirmed");
        result.Content.Should().Contain("150");
        result.Content.Should().Contain("BRL");
    }

    [Fact]
    public async Task ExecuteAsync_propagates_the_not_found_failure()
    {
        var dispatcher = new FakePaymentsRequestDispatcher();
        dispatcher.Stub.SetResponse(
            new GetPaymentStatusByReservationQuery(Context.ReservationId), Result.Failure<PaymentStatusResult>(new Error("pix_charge_not_found", "pix_charge_not_found")));
        var tool = new GetPaymentStatusTool(dispatcher);

        var result = await tool.ExecuteAsync(Context, null, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.FailureCode.Should().Be("pix_charge_not_found");
    }
}
