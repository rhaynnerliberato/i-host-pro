namespace IHostPro.BuildingBlocks.Infrastructure.Messaging;

/// <summary>
/// RabbitMQ **client-level** connection/channel timeouts — governs how long
/// an outbound send's opportunistic delivery attempt can block a caller
/// before Wolverine gives up and defers entirely to the durable outbox's
/// background durability agent. Not to be confused with message processing
/// timeouts or the Durability Agent's own polling cadence.
///
/// Bound directly from <c>IConfiguration</c> inside
/// <see cref="WolverineConfigurationExtensions.UseIHostProRabbitMq"/> —
/// never via <c>IOptions&lt;T&gt;</c> — because Wolverine's transport is
/// configured on <c>WolverineOptions</c> before the DI container is built
/// (<c>builder.Host.UseWolverine(...)</c> runs on <c>WebApplicationBuilder</c>,
/// prior to <c>.Build()</c>), so <c>IOptions&lt;T&gt;</c> is not yet
/// resolvable at that point. Still validated the same way every other
/// Options class in this solution is (bounds checked, fails fast with a
/// clear message) — just synchronously, via
/// <see cref="RabbitMqClientTimeoutOptionsValidator.ValidateAndThrow"/>,
/// instead of through <c>ValidateOnStart()</c>.
///
/// Confirmed empirically (Incremento 2 plan, homologação real) that
/// RabbitMQ.Client's own defaults (<c>RequestedConnectionTimeout</c>=30s,
/// <c>ContinuationTimeout</c>=20s) make an opportunistic send attempt block
/// the originating HTTP request for tens of seconds to minutes when the
/// broker is unreachable via a "silent" network partition (as opposed to an
/// immediate connection refusal, which already failed fast even with the
/// defaults) — the outbox itself was always correct; only these client
/// timeouts needed tuning.
///
/// <see cref="ContinuationTimeout"/> is the dominant factor once a
/// connection is already established (an AMQP channel-open on that
/// connection is a synchronous RPC governed entirely by it) — this is what a
/// paused-but-still-"connected" broker exercises, and by far the most common
/// outage shape (a silent network partition, not a clean process exit).
/// <see cref="ConnectTimeout"/> only governs establishing a brand new TCP
/// connection, which is already fast to fail when the broker is actually
/// down (connection refused is near-instant regardless of the configured
/// ceiling) — confirmed empirically with the broker fully stopped, not just
/// paused. Combined with <c>CircuitBreaking(cb => cb.FailuresBeforeCircuitBreaks = 1)</c>
/// on every route (Program.cs), which caps the opportunistic attempt to a
/// single try instead of Wolverine's default of three, 300ms was the
/// smallest value that passed the &lt;1s objective stably across 5 repeated
/// runs of the worst case (two events in one transaction, broker paused) —
/// 500ms did not consistently clear 1s; see Fase 1 doc, Seção 11 for the
/// measured figures across every step of this tuning.
/// </summary>
public sealed class RabbitMqClientTimeoutOptions
{
    public const string SectionName = "RabbitMq";

    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(2);
    public TimeSpan ContinuationTimeout { get; set; } = TimeSpan.FromMilliseconds(300);
}
