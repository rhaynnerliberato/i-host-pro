using FluentAssertions;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.AIAgent.Application.Tools;
using IHostPro.Contexts.AIAgent.Infrastructure.Tools;
using IHostPro.Contexts.GuestOperations.Application;

namespace IHostPro.Contexts.AIAgent.Tests.Unit.Tools;

public class RequestLateCheckoutToolTests
{
    private static readonly AgentToolContext Context = new(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

    private const string RequestedCheckOutAtRaw = "2026-09-05T14:00:00Z";

    [Fact]
    public void BuildSanitizedArguments_returns_the_canonical_payload_for_valid_input()
    {
        var tool = new RequestLateCheckoutTool(new FakeGuestOperationsRequestDispatcher());

        var result = tool.BuildSanitizedArguments(new Dictionary<string, string> { ["requestedCheckOutAt"] = RequestedCheckOutAtRaw });

        result.IsSuccess.Should().BeTrue();
        result.SanitizedArgumentsJson.Should().Contain("requestedCheckOutAt");
    }

    [Fact]
    public void BuildSanitizedArguments_fails_when_the_argument_is_missing()
    {
        var tool = new RequestLateCheckoutTool(new FakeGuestOperationsRequestDispatcher());

        var result = tool.BuildSanitizedArguments(null);

        result.IsSuccess.Should().BeFalse();
        result.FailureCode.Should().Be("missing_requested_check_out_at");
    }

    [Fact]
    public void BuildSanitizedArguments_fails_when_the_argument_is_malformed()
    {
        var tool = new RequestLateCheckoutTool(new FakeGuestOperationsRequestDispatcher());

        var result = tool.BuildSanitizedArguments(new Dictionary<string, string> { ["requestedCheckOutAt"] = "not-a-date" });

        result.IsSuccess.Should().BeFalse();
        result.FailureCode.Should().Be("invalid_requested_check_out_at");
    }

    [Fact]
    public async Task ExecuteAsync_an_approved_outcome_is_a_successful_tool_result()
    {
        var dispatcher = new FakeGuestOperationsRequestDispatcher();
        dispatcher.Stub.SetResponse(
            new RequestLateCheckoutCommand { TenantId = Context.TenantId, ReservationId = Context.ReservationId, RequestedCheckOutAt = DateTimeOffset.Parse(RequestedCheckOutAtRaw) },
            Result.Success(new LateCheckoutRequestResult(
                Guid.NewGuid(), Context.ReservationId, DateTimeOffset.Parse(RequestedCheckOutAtRaw), "none", null, false, "approved", null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)));
        var tool = new RequestLateCheckoutTool(dispatcher);

        var result = await tool.ExecuteAsync(Context, new Dictionary<string, string> { ["requestedCheckOutAt"] = RequestedCheckOutAtRaw }, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Content.Should().Contain("approved");
    }

    [Fact]
    public async Task ExecuteAsync_a_pending_payment_outcome_is_a_successful_tool_result_never_a_QR()
    {
        var dispatcher = new FakeGuestOperationsRequestDispatcher();
        dispatcher.Stub.SetResponse(
            new RequestLateCheckoutCommand { TenantId = Context.TenantId, ReservationId = Context.ReservationId, RequestedCheckOutAt = DateTimeOffset.Parse(RequestedCheckOutAtRaw) },
            Result.Success(new LateCheckoutRequestResult(
                Guid.NewGuid(), Context.ReservationId, DateTimeOffset.Parse(RequestedCheckOutAtRaw), "fixedAmount", 50m, true, "pending_payment", null, DateTimeOffset.UtcNow, null, DateTimeOffset.UtcNow)));
        var tool = new RequestLateCheckoutTool(dispatcher);

        var result = await tool.ExecuteAsync(Context, new Dictionary<string, string> { ["requestedCheckOutAt"] = RequestedCheckOutAtRaw }, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Content.Should().Contain("pending_payment");
        result.Content.Should().NotContainAny("qr", "QrCodePayload", "PIX_PAYLOAD", "copia-e-cola");
    }

    [Fact]
    public async Task ExecuteAsync_a_denied_outcome_is_still_a_successful_tool_result()
    {
        var dispatcher = new FakeGuestOperationsRequestDispatcher();
        dispatcher.Stub.SetResponse(
            new RequestLateCheckoutCommand { TenantId = Context.TenantId, ReservationId = Context.ReservationId, RequestedCheckOutAt = DateTimeOffset.Parse(RequestedCheckOutAtRaw) },
            Result.Success(new LateCheckoutRequestResult(
                Guid.NewGuid(), Context.ReservationId, DateTimeOffset.Parse(RequestedCheckOutAtRaw), "none", null, false, "denied", "ScheduleConflict", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)));
        var tool = new RequestLateCheckoutTool(dispatcher);

        var result = await tool.ExecuteAsync(Context, new Dictionary<string, string> { ["requestedCheckOutAt"] = RequestedCheckOutAtRaw }, CancellationToken.None);

        result.IsSuccess.Should().BeTrue("a business denial is a successful Tool execution, never a technical failure");
        result.Content.Should().Contain("denied").And.Contain("ScheduleConflict");
    }

    [Fact]
    public async Task ExecuteAsync_propagates_the_dispatcher_failure_code()
    {
        var dispatcher = new FakeGuestOperationsRequestDispatcher();
        dispatcher.Stub.SetResponse(
            new RequestLateCheckoutCommand { TenantId = Context.TenantId, ReservationId = Context.ReservationId, RequestedCheckOutAt = DateTimeOffset.Parse(RequestedCheckOutAtRaw) },
            Result.Failure<LateCheckoutRequestResult>(new Error("ReservationNotConfirmed", "ReservationNotConfirmed")));
        var tool = new RequestLateCheckoutTool(dispatcher);

        var result = await tool.ExecuteAsync(Context, new Dictionary<string, string> { ["requestedCheckOutAt"] = RequestedCheckOutAtRaw }, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.FailureCode.Should().Be("ReservationNotConfirmed");
    }
}
