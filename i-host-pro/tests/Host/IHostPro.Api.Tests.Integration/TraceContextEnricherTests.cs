using System.Diagnostics;
using FluentAssertions;
using IHostPro.Api.Observability;
using Serilog.Core;
using Serilog.Events;
using Serilog.Parsing;

namespace IHostPro.Api.Tests.Integration;

/// <summary>
/// Fase 12, Checkpoint 5.3E (Observability Architecture). Pure unit-style
/// tests — no fixture, no real Postgres/RabbitMQ/network — proving
/// <see cref="TraceContextEnricher"/>'s two documented behaviors directly:
/// it enriches from a real <see cref="Activity"/> when one is current, and
/// is a safe no-op otherwise. Constructs a bare <see cref="LogEvent"/>
/// directly (never via a full Serilog pipeline) since the enricher's own
/// contract is exactly this one method.
/// </summary>
public sealed class TraceContextEnricherTests
{
    private static readonly ActivitySource TestActivitySource = new("IHostPro.Api.Tests.Integration.TraceContextEnricherTests");

    private static LogEvent CreateLogEvent() => new(
        DateTimeOffset.UtcNow,
        LogEventLevel.Information,
        exception: null,
        new MessageTemplateParser().Parse("test"),
        properties: []);

    [Fact]
    public void Enrich_adds_TraceId_and_SpanId_matching_the_real_current_Activity()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(listener);

        using var activity = TestActivitySource.StartActivity("test-activity");
        activity.Should().NotBeNull("the listener above must allow the source to actually create one");

        var logEvent = CreateLogEvent();
        var enricher = new TraceContextEnricher();

        enricher.Enrich(logEvent, new FakePropertyFactory());

        logEvent.Properties.Should().ContainKey("TraceId");
        logEvent.Properties.Should().ContainKey("SpanId");
        ((ScalarValue)logEvent.Properties["TraceId"]).Value.Should().Be(Activity.Current!.TraceId.ToString());
        ((ScalarValue)logEvent.Properties["SpanId"]).Value.Should().Be(Activity.Current!.SpanId.ToString());
    }

    [Fact]
    public void Enrich_is_a_no_op_when_there_is_no_current_Activity()
    {
        Activity.Current = null;

        var logEvent = CreateLogEvent();
        var enricher = new TraceContextEnricher();

        var act = () => enricher.Enrich(logEvent, new FakePropertyFactory());

        act.Should().NotThrow();
        logEvent.Properties.Should().NotContainKey("TraceId");
        logEvent.Properties.Should().NotContainKey("SpanId");
    }

    /// <summary>Minimal stand-in for the real factory Serilog's own pipeline supplies — this test exercises the enricher in isolation, never a full logging pipeline.</summary>
    private sealed class FakePropertyFactory : ILogEventPropertyFactory
    {
        public LogEventProperty CreateProperty(string name, object? value, bool destructureObjects = false) =>
            new(name, new ScalarValue(value));
    }
}
