using System.Diagnostics.Metrics;

namespace IHostPro.BuildingBlocks.Infrastructure.Resilience;

/// <summary>
/// Fase 12, Checkpoint 3 (Resilience &amp; Rate Limiting), Decision Gate
/// amendment — shared, low-cardinality telemetry for the two official
/// (<c>Microsoft.Extensions.Http.Resilience</c>) circuit breakers this
/// checkpoint adds (Anthropic in <c>AIAgent.Infrastructure</c>, Meta in
/// <c>ExternalIntegrations.Infrastructure</c>). Lives here — metrics-only,
/// no Polly type in its signature — specifically so it can be shared without
/// making Polly/Resilience a transitive dependency of every project that
/// already references <c>BuildingBlocks.Infrastructure</c> platform-wide;
/// only the two Infrastructure projects that actually wire a circuit
/// breaker call into this class.
///
/// Tags are exactly <c>provider</c> (a fixed small enum: "Anthropic",
/// "Meta") and <c>state</c>/nothing else — never a URL, header, prompt,
/// response body, phone number, or any other unbounded/sensitive value.
/// "Rejected" (a call short-circuited because the breaker is already OPEN)
/// is deliberately NOT a separate metric here — both call sites already
/// have their own existing per-call outcome counter (e.g.
/// <c>ai_agent.model_calls</c>) and record a dedicated "CircuitOpen" outcome
/// value there instead, reusing infrastructure rather than adding a second
/// counter for the same fact.
/// </summary>
public static class CircuitBreakerTelemetry
{
    private static readonly Meter Meter = new("IHostPro.Resilience");
    private static readonly Counter<long> StateChanges = Meter.CreateCounter<long>("circuit_breaker.state_changes");

    public static void RecordStateChange(string provider, string state) =>
        StateChanges.Add(1,
            new KeyValuePair<string, object?>("provider", provider),
            new KeyValuePair<string, object?>("state", state));
}
