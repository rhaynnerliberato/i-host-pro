namespace IHostPro.Contexts.AIAgent.Application;

/// <summary>
/// Fase 11, Checkpoint 2 (AI Agent Foundation) — mandate item 15. Builds the
/// minimal <see cref="ModelRequest"/> CP2 needs: sanitized Conversation
/// history only. Deliberately does NOT consult Reservations/GuestOperations/
/// Payments/Housekeeping/PropertyManagement/Policies via Tools — that is
/// Checkpoint 3's scope.
/// </summary>
public interface IAgentContextBuilder
{
    Task<ModelRequest> BuildAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken);
}
