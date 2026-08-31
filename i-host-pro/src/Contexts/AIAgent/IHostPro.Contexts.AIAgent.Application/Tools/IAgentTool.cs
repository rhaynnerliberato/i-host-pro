namespace IHostPro.Contexts.AIAgent.Application.Tools;

/// <summary>
/// A single Read Tool the model loop may invoke (Fase 11, Checkpoint 3).
/// Each implementation calls its owning Bounded Context's own existing
/// Application Query through that context's <c>I&lt;Context&gt;RequestDispatcher</c>
/// — the generic Tools-&gt;Application-Service execution pattern already
/// authorized by Architecture Principles' Exception 3. No implementation may
/// reach a purpose-limited Contracts-tier reader belonging to a different
/// consumer (e.g. <c>IReservationScheduleReader</c>, <c>IPropertyGuestAccessReader</c>).
///
/// <paramref name="arguments"/> is a minimal, provider-neutral string bag —
/// most tools take none at all; the few that do (e.g. an optional policy
/// code) validate against a fixed allowlist, never free-form input trusted
/// blindly. Every identifier the tool actually needs to scope its query
/// (TenantId/ReservationId/etc.) comes from <see cref="AgentToolContext"/>,
/// never from <paramref name="arguments"/>.
/// </summary>
public interface IAgentTool
{
    AgentToolDescriptor Descriptor { get; }

    Task<AgentToolResult> ExecuteAsync(
        AgentToolContext context,
        IReadOnlyDictionary<string, string>? arguments,
        CancellationToken cancellationToken);
}
