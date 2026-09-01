using FluentAssertions;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.AIAgent.Infrastructure.ResponseDelivery;
using IHostPro.Contexts.Communication.Application;
using Mediator;

namespace IHostPro.Contexts.AIAgent.Tests.Unit.ResponseDelivery;

/// <summary>
/// Fase 11, Checkpoint 5 (mandate item 32): <see cref="AgentResponseDeliveryService"/>
/// retries the underlying <see cref="SendAgentResponseCommand"/> call at most
/// once when the failure looks technical — <see cref="SendAgentResponseCommand"/>'s
/// own idempotency key is deterministic (Tenant/AgentInteraction/Channel), so
/// an identical retry naturally reuses it, never creating a second Message. A
/// permanent/data-state failure is never retried.
/// </summary>
public class AgentResponseDeliveryServiceTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid ConversationId = Guid.NewGuid();
    private static readonly Guid ReservationId = Guid.NewGuid();
    private static readonly Guid AgentInteractionId = Guid.NewGuid();
    private const string Content = "Sua solicitação foi processada.";

    private sealed class ScriptedCommunicationRequestDispatcher : ICommunicationRequestDispatcher
    {
        private readonly Queue<Result<SendAgentResponseResult>> _responses;

        public ScriptedCommunicationRequestDispatcher(params Result<SendAgentResponseResult>[] responses) =>
            _responses = new Queue<Result<SendAgentResponseResult>>(responses);

        public int CallCount { get; private set; }

        public ValueTask<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            CallCount++;
            var response = _responses.Dequeue();
            return ValueTask.FromResult((TResponse)(object)response);
        }
    }

    [Fact]
    public async Task SendAsync_retries_once_on_a_technical_failure_and_succeeds()
    {
        var messageId = Guid.NewGuid();
        var dispatcher = new ScriptedCommunicationRequestDispatcher(
            Result.Failure<SendAgentResponseResult>(new Error("connector_exception", "connector_exception")),
            Result.Success(new SendAgentResponseResult(messageId)));
        var service = new AgentResponseDeliveryService(dispatcher);

        var result = await service.SendAsync(TenantId, ConversationId, ReservationId, AgentInteractionId, Content, CancellationToken.None);

        dispatcher.CallCount.Should().Be(2, "the first technical failure is retried exactly once");
        result.IsSuccess.Should().BeTrue();
        result.MessageId.Should().Be(messageId);
    }

    [Fact]
    public async Task SendAsync_never_retries_a_permanent_ConversationNotFound_failure()
    {
        var dispatcher = new ScriptedCommunicationRequestDispatcher(
            Result.Failure<SendAgentResponseResult>(new Error("ConversationNotFound", "ConversationNotFound")));
        var service = new AgentResponseDeliveryService(dispatcher);

        var result = await service.SendAsync(TenantId, ConversationId, ReservationId, AgentInteractionId, Content, CancellationToken.None);

        dispatcher.CallCount.Should().Be(1, "a permanent/data-state failure would fail identically again — never worth a retry");
        result.IsSuccess.Should().BeFalse();
        result.FailureCode.Should().Be("ConversationNotFound");
    }

    [Fact]
    public async Task SendAsync_never_retries_a_permanent_GuestContactOrPhoneNotAvailable_failure()
    {
        var dispatcher = new ScriptedCommunicationRequestDispatcher(
            Result.Failure<SendAgentResponseResult>(new Error("GuestContactOrPhoneNotAvailable", "GuestContactOrPhoneNotAvailable")));
        var service = new AgentResponseDeliveryService(dispatcher);

        var result = await service.SendAsync(TenantId, ConversationId, ReservationId, AgentInteractionId, Content, CancellationToken.None);

        dispatcher.CallCount.Should().Be(1);
        result.IsSuccess.Should().BeFalse();
        result.FailureCode.Should().Be("GuestContactOrPhoneNotAvailable");
    }

    [Fact]
    public async Task SendAsync_returns_failure_once_the_retry_also_fails_technically()
    {
        var dispatcher = new ScriptedCommunicationRequestDispatcher(
            Result.Failure<SendAgentResponseResult>(new Error("connector_exception", "connector_exception")),
            Result.Failure<SendAgentResponseResult>(new Error("connector_rejected", "connector_rejected")));
        var service = new AgentResponseDeliveryService(dispatcher);

        var result = await service.SendAsync(TenantId, ConversationId, ReservationId, AgentInteractionId, Content, CancellationToken.None);

        dispatcher.CallCount.Should().Be(2, "exactly one retry — never more");
        result.IsSuccess.Should().BeFalse();
        result.FailureCode.Should().Be("connector_rejected");
    }

    [Fact]
    public async Task SendAsync_succeeds_on_the_first_call_without_any_retry()
    {
        var messageId = Guid.NewGuid();
        var dispatcher = new ScriptedCommunicationRequestDispatcher(Result.Success(new SendAgentResponseResult(messageId)));
        var service = new AgentResponseDeliveryService(dispatcher);

        var result = await service.SendAsync(TenantId, ConversationId, ReservationId, AgentInteractionId, Content, CancellationToken.None);

        dispatcher.CallCount.Should().Be(1);
        result.IsSuccess.Should().BeTrue();
        result.MessageId.Should().Be(messageId);
    }
}
