namespace IHostPro.Contexts.AIAgent.Application.Tools;

/// <summary>
/// The exact, closed set of Tools approved for Fase 11 (Checkpoint 3 — the 8
/// Read Tools; Checkpoint 4 — the 3 approved business Write Tools). No more,
/// no less; adding another tool is out of scope and requires a new mandate.
/// Used both by the concrete <see cref="IAgentTool"/> implementations' own
/// <see cref="AgentToolDescriptor.Name"/> and by architecture tests to prove
/// the surface never silently grows.
///
/// <see cref="RequestGuestAccessDelivery"/> is deliberately distinct from
/// the Checkpoint 3 Read Tool <c>GetAccessInstructions</c> — one informs
/// (reads free-text instructions), the other triggers the real secure
/// delivery choreography (writes).
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

    /// <summary>Fase 11, Checkpoint 4 — REQUIRED_CP4, CONFIRMATION_REQUIRED.</summary>
    public const string RequestEarlyCheckIn = "RequestEarlyCheckIn";

    /// <summary>Fase 11, Checkpoint 4 — REQUIRED_CP4, CONFIRMATION_REQUIRED.</summary>
    public const string RequestLateCheckout = "RequestLateCheckout";

    /// <summary>Fase 11, Checkpoint 4 — REQUIRED_CP4, EXPLICIT_REQUEST_IS_CONFIRMATION (executes immediately, never a pending action).</summary>
    public const string RequestGuestAccessDelivery = "RequestGuestAccessDelivery";
}
