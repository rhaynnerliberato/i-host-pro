using IHostPro.BuildingBlocks.Application;
using IHostPro.Contexts.AIAgent.Domain;
using IHostPro.Contexts.Communication.Contracts;
using Microsoft.Extensions.Logging;

namespace IHostPro.Contexts.AIAgent.Application;

/// <summary>
/// Reacts to <see cref="ConversationMessageReceived"/> (Fase 11, Checkpoint 2
/// — AI Agent Foundation), the real session-creation flow (mandate item 14/25):
/// resolve/create the active <see cref="AgentSession"/> → read sanitized
/// conversation history (ADR-030) → build minimal context → call
/// <see cref="IModelProvider"/> → persist <see cref="AgentInteraction"/>.
/// NEVER sends anything to the guest — no Communication outbound action
/// (mandate item 26), response delivery is Checkpoint 4's scope.
///
/// Idempotency (mandate item 19/28): looked up by
/// <c>TenantId</c>/<c>InboundMessageId</c> BEFORE resolving a session or
/// calling the model provider — a redelivered <c>ConversationMessageReceived</c>
/// is a silent, zero-effect no-op, exactly like every other idempotency check
/// in this codebase (never a second model call, mandate item 19's own
/// "ModelProvider chamado: 1 vez").
///
/// Failure (mandate item 20): a <see cref="ModelProviderException"/> (Fake
/// provider controlled failure) persists a <see cref="AgentInteractionOutcome.Failure"/>
/// <see cref="AgentInteraction"/> — the session itself is left untouched
/// ("permanece consistente"): no confirmed language/intent/confidence exists
/// to record from a failed call. No outbound Message, no automatic handoff,
/// no retry loop.
/// </summary>
public sealed class ConversationMessageReceivedProcessor : IIntegrationEventHandler<ConversationMessageReceived>
{
    private const string AlreadyProcessedReason = "AlreadyProcessed";

    private readonly IAgentSessionResolver _sessionResolver;
    private readonly IAgentSessionRepository _sessionRepository;
    private readonly IAgentInteractionRepository _interactionRepository;
    private readonly IAgentContextBuilder _contextBuilder;
    private readonly IModelProvider _modelProvider;
    private readonly IAIAgentTransactionExecutor _transactionExecutor;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ConversationMessageReceivedProcessor> _logger;

    public ConversationMessageReceivedProcessor(
        IAgentSessionResolver sessionResolver,
        IAgentSessionRepository sessionRepository,
        IAgentInteractionRepository interactionRepository,
        IAgentContextBuilder contextBuilder,
        IModelProvider modelProvider,
        IAIAgentTransactionExecutor transactionExecutor,
        TimeProvider timeProvider,
        ILogger<ConversationMessageReceivedProcessor> logger)
    {
        _sessionResolver = sessionResolver;
        _sessionRepository = sessionRepository;
        _interactionRepository = interactionRepository;
        _contextBuilder = contextBuilder;
        _modelProvider = modelProvider;
        _transactionExecutor = transactionExecutor;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task HandleAsync(ConversationMessageReceived @event, CancellationToken cancellationToken)
    {
        var existing = await _transactionExecutor.ExecuteAsync(
            () => _interactionRepository.GetByInboundMessageIdAsync(@event.TenantId, @event.MessageId, cancellationToken),
            cancellationToken);
        if (existing is not null)
        {
            _logger.LogInformation(
                "AIAgent {Trigger} skipped for tenant {TenantId}: {Result}",
                nameof(ConversationMessageReceived), @event.TenantId, AlreadyProcessedReason);
            return;
        }

        var sessionId = await _sessionResolver.GetOrCreateActiveSessionIdAsync(
            @event.TenantId, @event.ConversationId, @event.ReservationId, @event.OccurredAtUtc, cancellationToken);

        var request = await _contextBuilder.BuildAsync(@event.TenantId, @event.ConversationId, cancellationToken);

        var interactionId = Guid.NewGuid();
        var startedAtUtc = _timeProvider.GetUtcNow();

        try
        {
            var result = await _modelProvider.GenerateAsync(request, cancellationToken);
            var completedAtUtc = _timeProvider.GetUtcNow();

            await _transactionExecutor.ExecuteAsync(async () =>
            {
                var interaction = AgentInteraction.Start(
                    interactionId, @event.TenantId, sessionId, @event.MessageId,
                    _modelProvider.ProviderName, result.ModelName, startedAtUtc);
                interaction.CompleteSuccessfully(
                    completedAtUtc, result.Intent, result.DetectedLanguage, result.Confidence,
                    result.InputTokens, result.OutputTokens);
                _interactionRepository.Add(interaction);

                var session = (await _sessionRepository.GetByIdAsync(sessionId, cancellationToken))!;
                session.RecordInteraction(
                    completedAtUtc, result.DetectedLanguage, result.Intent, result.Confidence,
                    _modelProvider.ProviderName, result.ModelName);
                _sessionRepository.Update(session);

                return true;
            }, cancellationToken);

            _logger.LogInformation(
                "AIAgent {Trigger} outcome for tenant {TenantId} conversationId {ConversationId} sessionId {SessionId} interactionId {InteractionId}: {Result}",
                nameof(ConversationMessageReceived), @event.TenantId, @event.ConversationId, sessionId, interactionId, "InteractionSucceeded");
        }
        catch (ModelProviderException)
        {
            var completedAtUtc = _timeProvider.GetUtcNow();

            await _transactionExecutor.ExecuteAsync(() =>
            {
                var interaction = AgentInteraction.Start(
                    interactionId, @event.TenantId, sessionId, @event.MessageId,
                    _modelProvider.ProviderName, _modelProvider.ModelName, startedAtUtc);
                interaction.CompleteWithFailure(completedAtUtc);
                _interactionRepository.Add(interaction);

                return Task.FromResult(true);
            }, cancellationToken);

            _logger.LogWarning(
                "AIAgent {Trigger} outcome for tenant {TenantId} conversationId {ConversationId} sessionId {SessionId} interactionId {InteractionId}: {Result}",
                nameof(ConversationMessageReceived), @event.TenantId, @event.ConversationId, sessionId, interactionId, "InteractionFailed");
        }
    }
}
