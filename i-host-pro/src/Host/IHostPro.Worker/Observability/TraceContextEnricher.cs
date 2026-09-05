using System.Diagnostics;
using Serilog.Core;
using Serilog.Events;

namespace IHostPro.Worker.Observability;

/// <summary>
/// Fase 12, Checkpoint 5.3E (Observability Architecture) — closes
/// <c>CorrelationGapConfirmed</c>: structured logs previously carried no
/// distributed-trace identifier at all, despite the OTel SDK already being
/// registered. Wolverine's own <c>ActivitySource</c> (already listened to
/// via <c>AddSource("Wolverine")</c>) starts a real <see cref="Activity"/>
/// per message handled, so <see cref="Activity.Current"/> is populated for
/// every message-processing log line, the same way it already is for every
/// HTTP request in <c>IHostPro.Api</c>. Adds <c>TraceId</c>/<c>SpanId</c> as
/// plain structured properties — never a new correlation concept, never a
/// replacement for <c>IntegrationEvent.CorrelationId</c> (a separate,
/// business-level identifier that continues unchanged).
///
/// A no-op when <see cref="Activity.Current"/> is null (e.g. a background
/// startup log line with no message/activity in scope) — never throws,
/// never fabricates an identifier. Never touches any other property, so it
/// can never leak PII/secrets by construction.
/// </summary>
public sealed class TraceContextEnricher : ILogEventEnricher
{
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        var activity = Activity.Current;
        if (activity is null)
            return;

        logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty("TraceId", activity.TraceId.ToString()));
        logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty("SpanId", activity.SpanId.ToString()));
    }
}
