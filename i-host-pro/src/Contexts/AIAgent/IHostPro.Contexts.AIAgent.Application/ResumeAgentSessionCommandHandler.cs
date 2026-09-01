using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.AIAgent.Domain;

namespace IHostPro.Contexts.AIAgent.Application;

/// <summary>
/// Handles <see cref="ResumeAgentSessionCommand"/> (Fase 11, Checkpoint 6).
/// Resume is only valid when the session is genuinely
/// <see cref="AgentSessionStatus.Escalated"/> and a real, still-active
/// <see cref="AgentHumanHandoff"/> exists for it (CP6 mandate item 36) — both
/// transitions are applied atomically, in the same transaction. Never reopens
/// the <see cref="AgentPendingAction"/>(s) the handoff already cancelled
/// (CP6 mandate item 37) — this handler never touches that repository at
/// all.
/// </summary>
public sealed class ResumeAgentSessionCommandHandler : ICommandHandler<ResumeAgentSessionCommand, ResumeAgentSessionResult>
{
    private static readonly Error AgentSessionNotFoundError = new("AgentSessionNotFound", "AgentSessionNotFound");
    private static readonly Error NoActiveHumanHandoffError = new("NoActiveHumanHandoff", "NoActiveHumanHandoff");

    private readonly IAgentSessionRepository _sessionRepository;
    private readonly IAgentHumanHandoffRepository _handoffRepository;
    private readonly IAIAgentTransactionExecutor _transactionExecutor;
    private readonly TimeProvider _timeProvider;

    public ResumeAgentSessionCommandHandler(
        IAgentSessionRepository sessionRepository, IAgentHumanHandoffRepository handoffRepository,
        IAIAgentTransactionExecutor transactionExecutor, TimeProvider timeProvider)
    {
        _sessionRepository = sessionRepository;
        _handoffRepository = handoffRepository;
        _transactionExecutor = transactionExecutor;
        _timeProvider = timeProvider;
    }

    public ValueTask<Result<ResumeAgentSessionResult>> Handle(ResumeAgentSessionCommand command, CancellationToken cancellationToken) =>
        new(_transactionExecutor.ExecuteAsync(async () =>
        {
            var session = await _sessionRepository.GetByIdAsync(command.AgentSessionId, cancellationToken);
            if (session is null || session.TenantId != command.TenantId)
                return Result.Failure<ResumeAgentSessionResult>(AgentSessionNotFoundError);

            var handoff = await _handoffRepository.GetActiveByAgentSessionIdAsync(command.AgentSessionId, cancellationToken);
            if (handoff is null)
                return Result.Failure<ResumeAgentSessionResult>(NoActiveHumanHandoffError);

            var now = _timeProvider.GetUtcNow();

            handoff.Resume(now, command.ActorId);
            _handoffRepository.Update(handoff);

            session.Resume(now);
            _sessionRepository.Update(session);

            return Result.Success(new ResumeAgentSessionResult(session.Id, handoff.Id, now));
        }, cancellationToken));
}
