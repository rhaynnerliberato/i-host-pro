namespace IHostPro.Contexts.AIAgent.Infrastructure.ModelProviders.Anthropic;

/// <summary>
/// Infrastructure-only technical configuration for the real Anthropic
/// Messages API client (Fase 11, Checkpoint 7, mandate item 51 — never
/// mixed with <c>AI_AGENT_BEHAVIOR</c> business configuration, which lives in
/// Configuration &amp; Policy instead). Bound from <c>AIAgent:Anthropic</c> —
/// everything here is non-secret; the API key itself is resolved separately,
/// exclusively through <see cref="IAnthropicCredentialProvider"/>, never a
/// field on this type.
/// </summary>
public sealed class AnthropicOptions
{
    /// <summary>No trailing slash — <see cref="MessagesPath"/> is appended to it as a relative path.</summary>
    public string BaseUrl { get; set; } = "https://api.anthropic.com";

    public const string MessagesPath = "v1/messages";

    /// <summary>Confirmed current/latest as of this checkpoint's own governance record — see the Fase 11 CP7 homologation document.</summary>
    public string ApiVersion { get; set; } = "2023-06-01";

    /// <summary>
    /// The exact, pinned model id (mandate item 2) — <c>claude-sonnet-4-6</c>.
    /// Never changed silently; a real API rejection of this exact id is a
    /// governance stop (mandate item 76), not a reason to substitute another
    /// model automatically.
    /// </summary>
    public string Model { get; set; } = "claude-sonnet-4-6";

    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>Provider technical config (mandate item 20) — never confused with a business/conversational response-length policy.</summary>
    public int MaxTokens { get; set; } = 2048;

    public AnthropicPricingOptions Pricing { get; set; } = new();

    /// <summary>
    /// Fase 12, Checkpoint 3 (Resilience &amp; Rate Limiting) — Decision Gate
    /// amendment: HTTP circuit breaking via the official
    /// <c>Microsoft.Extensions.Http.Resilience</c> package (never a
    /// hand-rolled implementation, never a retry/hedging/fallback/timeout
    /// stage — see <c>AIAgentModuleExtensions</c>'s own wiring comment).
    /// </summary>
    public HttpCircuitBreakerOptions CircuitBreaker { get; set; } = new();
}

/// <summary>
/// No production-grade threshold is decided by this checkpoint — every
/// default here is conservative for dev/homologation
/// (<c>ProductionCircuitBreakerThresholdsRequired=true</c>, mirroring every
/// other CP3 threshold's own reasoning).
/// </summary>
public sealed class HttpCircuitBreakerOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>Fraction of calls (within <see cref="SamplingDuration"/>) that must be classified a failure before the circuit opens.</summary>
    public double FailureRatio { get; set; } = 0.5;

    /// <summary>Minimum number of calls within <see cref="SamplingDuration"/> before the failure ratio is even evaluated — avoids opening the circuit on a tiny, statistically meaningless sample.</summary>
    public int MinimumThroughput { get; set; } = 4;

    public TimeSpan SamplingDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How long the circuit stays OPEN before a single probe call (HALF-OPEN) is allowed through.</summary>
    public TimeSpan BreakDuration { get; set; } = TimeSpan.FromSeconds(15);
}

/// <summary>
/// Real pricing confirmed for <c>claude-sonnet-4-6</c> at the time of this
/// checkpoint's own governance record (platform.claude.com/docs — see the
/// Fase 11 CP7 homologation document for the exact source/date). Never
/// hardcoded in Domain — this is Infrastructure-only technical
/// configuration, mirroring mandate item 38.
/// </summary>
public sealed class AnthropicPricingOptions
{
    public decimal InputUsdPerMillionTokens { get; set; } = 3m;

    public decimal OutputUsdPerMillionTokens { get; set; } = 15m;

    /// <summary>Persisted verbatim as <c>AgentInteraction.CostPricingReference</c> — identifies which pricing table produced a given <c>EstimatedCostUsd</c>, never a magic/undocumented number.</summary>
    public string Reference { get; set; } = "claude-sonnet-4-6";
}
