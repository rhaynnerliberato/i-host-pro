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
            new RequestGuestAccessDeliveryCommand
            {
                TenantId = Context.TenantId,
                ReservationId = Context.ReservationId,
                ActorType = "AI",
                ActorId = Context.AgentSessionId.ToString(),
            },
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
            new RequestGuestAccessDeliveryCommand
            {
                TenantId = Context.TenantId,
                ReservationId = Context.ReservationId,
                ActorType = "AI",
                ActorId = Context.AgentSessionId.ToString(),
            },
            Result.Success(new GuestStayOperationResult(
                Guid.NewGuid(), Context.ReservationId, Guid.NewGuid(), "active", null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)));
        var tool = new RequestGuestAccessDeliveryTool(dispatcher);

        var result = await tool.ExecuteAsync(Context, null, CancellationToken.None);

        result.Content.Should().NotContainAny("AccessCredential", "SecretReference", "vault://", "senha:");
    }

    [Fact]
    public async Task ExecuteAsync_always_identifies_itself_as_the_AI_actor_never_a_human_user()
    {
        // Fase 12, Checkpoint 4 (Security/Secrets/LGPD Hardening) — the AI
        // Agent's own session id, never a fabricated human User id, is what
        // must reach the command when this Tool is the caller.
        var dispatcher = new FakeGuestOperationsRequestDispatcher();
        dispatcher.Stub.SetResponse(
            new RequestGuestAccessDeliveryCommand
            {
                TenantId = Context.TenantId,
                ReservationId = Context.ReservationId,
                ActorType = "AI",
                ActorId = Context.AgentSessionId.ToString(),
            },
            Result.Success(new GuestStayOperationResult(
                Guid.NewGuid(), Context.ReservationId, Guid.NewGuid(), "active", null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)));
        var tool = new RequestGuestAccessDeliveryTool(dispatcher);

        var result = await tool.ExecuteAsync(Context, null, CancellationToken.None);

        result.IsSuccess.Should().BeTrue("the dispatcher stub only matches a command carrying exactly ActorType=\"AI\" and the session id");
    }

    [Fact]
    public async Task ExecuteAsync_propagates_the_dispatcher_failure_code()
    {
        var dispatcher = new FakeGuestOperationsRequestDispatcher();
        dispatcher.Stub.SetResponse(
            new RequestGuestAccessDeliveryCommand
            {
                TenantId = Context.TenantId,
                ReservationId = Context.ReservationId,
                ActorType = "AI",
                ActorId = Context.AgentSessionId.ToString(),
            },
            Result.Failure<GuestStayOperationResult>(new Error("GuestStayOperationAlreadyCheckedOut", "GuestStayOperationAlreadyCheckedOut")));
        var tool = new RequestGuestAccessDeliveryTool(dispatcher);

        var result = await tool.ExecuteAsync(Context, null, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.FailureCode.Should().Be("GuestStayOperationAlreadyCheckedOut");
    }
}
