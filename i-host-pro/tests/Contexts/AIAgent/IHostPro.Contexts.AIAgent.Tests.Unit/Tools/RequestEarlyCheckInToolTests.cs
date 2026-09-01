using FluentAssertions;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.AIAgent.Application.Tools;
using IHostPro.Contexts.AIAgent.Infrastructure.Tools;
using IHostPro.Contexts.GuestOperations.Application;

namespace IHostPro.Contexts.AIAgent.Tests.Unit.Tools;

public class RequestEarlyCheckInToolTests
{
    private static readonly AgentToolContext Context = new(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

    private const string RequestedCheckInAtRaw = "2026-09-01T12:00:00Z";

    [Fact]
    public void BuildSanitizedArguments_returns_the_canonical_payload_for_valid_input()
    {
        var tool = new RequestEarlyCheckInTool(new FakeGuestOperationsRequestDispatcher());

        var result = tool.BuildSanitizedArguments(new Dictionary<string, string> { ["requestedCheckInAt"] = RequestedCheckInAtRaw });

        result.IsSuccess.Should().BeTrue();
        result.SanitizedArgumentsJson.Should().Contain("requestedCheckInAt");
    }

    [Fact]
    public void BuildSanitizedArguments_fails_when_the_argument_is_missing()
    {
        var tool = new RequestEarlyCheckInTool(new FakeGuestOperationsRequestDispatcher());

        var result = tool.BuildSanitizedArguments(null);

        result.IsSuccess.Should().BeFalse();
        result.FailureCode.Should().Be("missing_requested_check_in_at");
    }

    [Fact]
    public void BuildSanitizedArguments_fails_when_the_argument_is_malformed()
    {
        var tool = new RequestEarlyCheckInTool(new FakeGuestOperationsRequestDispatcher());

        var result = tool.BuildSanitizedArguments(new Dictionary<string, string> { ["requestedCheckInAt"] = "not-a-date" });

        result.IsSuccess.Should().BeFalse();
        result.FailureCode.Should().Be("invalid_requested_check_in_at");
    }

    [Fact]
    public async Task ExecuteAsync_an_approved_outcome_is_a_successful_tool_result()
    {
        var dispatcher = new FakeGuestOperationsRequestDispatcher();
        dispatcher.Stub.SetResponse(
            new RequestEarlyCheckInCommand { TenantId = Context.TenantId, ReservationId = Context.ReservationId, RequestedCheckInAt = DateTimeOffset.Parse(RequestedCheckInAtRaw) },
            Result.Success(new EarlyCheckInRequestResult(
                Guid.NewGuid(), Context.ReservationId, DateTimeOffset.Parse(RequestedCheckInAtRaw), "approved", null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)));
        var tool = new RequestEarlyCheckInTool(dispatcher);

        var result = await tool.ExecuteAsync(Context, new Dictionary<string, string> { ["requestedCheckInAt"] = RequestedCheckInAtRaw }, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Content.Should().Contain("approved");
    }

    [Fact]
    public async Task ExecuteAsync_a_denied_outcome_is_still_a_successful_tool_result()
    {
        var dispatcher = new FakeGuestOperationsRequestDispatcher();
        dispatcher.Stub.SetResponse(
            new RequestEarlyCheckInCommand { TenantId = Context.TenantId, ReservationId = Context.ReservationId, RequestedCheckInAt = DateTimeOffset.Parse(RequestedCheckInAtRaw) },
            Result.Success(new EarlyCheckInRequestResult(
                Guid.NewGuid(), Context.ReservationId, DateTimeOffset.Parse(RequestedCheckInAtRaw), "denied", "CleaningNotReady", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)));
        var tool = new RequestEarlyCheckInTool(dispatcher);

        var result = await tool.ExecuteAsync(Context, new Dictionary<string, string> { ["requestedCheckInAt"] = RequestedCheckInAtRaw }, CancellationToken.None);

        result.IsSuccess.Should().BeTrue("a business denial is a successful Tool execution, never a technical failure");
        result.Content.Should().Contain("denied").And.Contain("CleaningNotReady");
    }

    [Fact]
    public async Task ExecuteAsync_propagates_the_dispatcher_failure_code()
    {
        var dispatcher = new FakeGuestOperationsRequestDispatcher();
        dispatcher.Stub.SetResponse(
            new RequestEarlyCheckInCommand { TenantId = Context.TenantId, ReservationId = Context.ReservationId, RequestedCheckInAt = DateTimeOffset.Parse(RequestedCheckInAtRaw) },
            Result.Failure<EarlyCheckInRequestResult>(new Error("ReservationNotConfirmed", "ReservationNotConfirmed")));
        var tool = new RequestEarlyCheckInTool(dispatcher);

        var result = await tool.ExecuteAsync(Context, new Dictionary<string, string> { ["requestedCheckInAt"] = RequestedCheckInAtRaw }, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.FailureCode.Should().Be("ReservationNotConfirmed");
    }

    [Fact]
    public async Task ExecuteAsync_fails_without_calling_the_dispatcher_when_the_argument_is_missing()
    {
        var dispatcher = new FakeGuestOperationsRequestDispatcher();
        var tool = new RequestEarlyCheckInTool(dispatcher);

        var result = await tool.ExecuteAsync(Context, null, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.FailureCode.Should().Be("missing_requested_check_in_at");
        dispatcher.Stub.ReceivedRequests.Should().BeEmpty();
    }
}
