namespace IHostPro.Contexts.AIAgent.Application.Tools;

/// <summary>
/// The exact, closed set of Read Tools approved for Fase 11, Checkpoint 3 —
/// Read Tools &amp; Context Builder. No more, no less; adding a ninth tool
/// (or a write tool) is out of this checkpoint's scope and requires a new
/// mandate. Used both by the concrete <see cref="IAgentTool"/> implementations'
/// own <see cref="AgentToolDescriptor.Name"/> and by
/// <c>AIAgentFoundationArchitectureTests</c> to prove the surface never
/// silently grows.
/// </summary>
public static class AgentToolNames
{
    public const string GetReservationSummary = "GetReservationSummary";
    public const string GetSchedule = "GetSchedule";
    public const string GetAvailability = "GetAvailability";
    public const string GetPropertyInformation = "GetPropertyInformation";
    public const string GetAccessInstructions = "GetAccessInstructions";
    public const string GetCleaningStatus = "GetCleaningStatus";
    public const string GetPaymentStatus = "GetPaymentStatus";
    public const string GetRelevantPolicies = "GetRelevantPolicies";
}
