using FluentAssertions;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.AIAgent.Application.Tools;
using IHostPro.Contexts.AIAgent.Infrastructure.Tools;
using IHostPro.Contexts.GuestOperations.Application;

namespace IHostPro.Contexts.AIAgent.Tests.Unit.Tools;

public class RequestGuestAccessDeliveryToolTests
{
    private static readonly AgentToolContext Context = new(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

    [Fact]
    public void Tool_is_not_IConfirmableAgentTool()
    {
        var tool = new RequestGuestAccessDeliveryTool(new FakeGuestOperationsRequestDispatcher());

        tool.Should().NotBeAssignableTo<IConfirmableAgentTool>(
            "EXPLICIT_REQUEST_IS_CONFIRMATION — the guest's own explicit request already is the confirmation, never a pending action");
    }

    [Fact]
    public async Task ExecuteAsync_sends_zero_identity_arguments_beyond_backend_derived_TenantId_ReservationId()
    {
        var dispatcher = new FakeGuestOperationsRequestDispatcher();
        dispatcher.Stub.SetResponse(
            new RequestGuestAccessDeliveryCommand { TenantId = Context.TenantId, ReservationId = Context.ReservationId },
            Result.Success(new GuestStayOperationResult(
                Guid.NewGuid(), Context.ReservationId, Guid.NewGuid(), "active", null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)));
        var tool = new RequestGuestAccessDeliveryTool(dispatcher);

        var result = await tool.ExecuteAsync(Context, null, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        dispatcher.Stub.ReceivedRequests.Should().ContainSingle();
    }

    [Fact]
    public async Task ExecuteAsync_result_never_contains_a_credential_or_secret_reference()
    {
        var dispatcher = new FakeGuestOperationsRequestDispatcher();
        dispatcher.Stub.SetResponse(
            new RequestGuestAccessDeliveryCommand { TenantId = Context.TenantId, ReservationId = Context.ReservationId },
            Result.Success(new GuestStayOperationResult(
                Guid.NewGuid(), Context.ReservationId, Guid.NewGuid(), "active", null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)));
        var tool = new RequestGuestAccessDeliveryTool(dispatcher);

        var result = await tool.ExecuteAsync(Context, null, CancellationToken.None);

        result.Content.Should().NotContainAny("AccessCredential", "SecretReference", "vault://", "senha:");
    }

    [Fact]
    public async Task ExecuteAsync_propagates_the_dispatcher_failure_code()
    {
        var dispatcher = new FakeGuestOperationsRequestDispatcher();
        dispatcher.Stub.SetResponse(
            new RequestGuestAccessDeliveryCommand { TenantId = Context.TenantId, ReservationId = Context.ReservationId },
            Result.Failure<GuestStayOperationResult>(new Error("GuestStayOperationAlreadyCheckedOut", "GuestStayOperationAlreadyCheckedOut")));
        var tool = new RequestGuestAccessDeliveryTool(dispatcher);

        var result = await tool.ExecuteAsync(Context, null, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.FailureCode.Should().Be("GuestStayOperationAlreadyCheckedOut");
    }
}
