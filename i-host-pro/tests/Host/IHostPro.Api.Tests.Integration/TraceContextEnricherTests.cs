using System.Diagnostics;
using FluentAssertions;
using IHostPro.Api.Observability;
using Serilog;
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

    /// <summary>
    /// The exact literal console output template Api/Worker's Program.cs
    /// configure (CP5.3E corrective fix). Duplicated here rather than shared
    /// — top-level statement locals aren't visible to a test project, and
    /// this mirrors the pre-existing convention of duplicating small
    /// host-specific observability plumbing (e.g. TraceContextEnricher
    /// itself exists once per host, not in a shared project).
    /// </summary>
    private const string ConsoleOutputTemplate = "[{Timestamp:HH:mm:ss} {Level:u3}] [TraceId={TraceId}] [SpanId={SpanId}] {Message:lj}{NewLine}{Exception}";

    /// <summary>
    /// Exercises the REAL Serilog pipeline (LoggerConfiguration -> Enrich ->
    /// Console sink with the exact outputTemplate Program.cs configures) —
    /// deliberately not the bare-LogEvent + FakePropertyFactory + direct
    /// MessageTemplateTextFormatter construction the tests above use, which
    /// was empirically found to not render named custom properties even when
    /// logEvent.Properties genuinely contains them (an artifact of bypassing
    /// the pipeline, not a real product bug). Redirects Console.Out to a
    /// StringWriter for the duration of the test only, then restores it.
    /// </summary>
    [Fact]
    public void ConsoleOutputTemplate_renders_the_real_TraceId_and_SpanId_when_enriched()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(listener);

        using var activity = TestActivitySource.StartActivity("test-activity-render");
        activity.Should().NotBeNull("the listener above must allow the source to actually create one");

        var originalOut = Console.Out;
        using var writer = new StringWriter();
        string rendered;
        try
        {
            Console.SetOut(writer);
            using var logger = new LoggerConfiguration()
                .Enrich.With(new TraceContextEnricher())
                .WriteTo.Console(outputTemplate: ConsoleOutputTemplate)
                .CreateLogger();

            logger.Information("test");
            rendered = writer.ToString();
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        rendered.Should().Contain(Activity.Current!.TraceId.ToString());
        rendered.Should().Contain(Activity.Current!.SpanId.ToString());
    }

    [Fact]
    public void ConsoleOutputTemplate_renders_without_throwing_when_there_is_no_current_Activity()
    {
        Activity.Current = null;

        var originalOut = Console.Out;
        using var writer = new StringWriter();
        try
        {
            Console.SetOut(writer);
            using var logger = new LoggerConfiguration()
                .Enrich.With(new TraceContextEnricher())
                .WriteTo.Console(outputTemplate: ConsoleOutputTemplate)
                .CreateLogger();

            var act = () => logger.Information("test");

            act.Should().NotThrow();
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    /// <summary>Minimal stand-in for the real factory Serilog's own pipeline supplies — this test exercises the enricher in isolation, never a full logging pipeline.</summary>
    private sealed class FakePropertyFactory : ILogEventPropertyFactory
    {
        public LogEventProperty CreateProperty(string name, object? value, bool destructureObjects = false) =>
            new(name, new ScalarValue(value));
    }
}
