using System.Text.Json;
using System.Text.RegularExpressions;
using IHostPro.Contexts.ExternalIntegrations.Application.WhatsAppTenantRoutes;
using IHostPro.Contexts.ExternalIntegrations.Domain;

namespace IHostPro.Contexts.ExternalIntegrations.Infrastructure.Meta;

/// <summary>
/// Meta-specific implementation of <see cref="IWhatsAppWebhookMessageProcessor"/>
/// (Fase 11, Checkpoint 1). All Meta envelope parsing lives here, entirely
/// confined to <c>Infrastructure.Meta</c> — mirrors
/// <see cref="MetaWebhookStatusProcessor"/>'s own structure exactly, parsing
/// the SAME raw body independently for the <c>messages[]</c> array rather
/// than sharing a parse pass (small, tenant-scoped payload; keeps each
/// processor single-purpose and independently testable).
///
/// Deliberately foundation-only: resolves tenant per message entry and
/// normalizes/classifies it — never creates a <c>Conversation</c>/<c>Message</c>,
/// never persists a dedup receipt, never publishes anything (that is
/// <c>Communication</c>/the event publisher's job).
///
/// Phone normalization: reduces Meta's <c>from</c> field to digits-only.
/// This is the SAME rule <c>Reservations.Infrastructure.Communication.ReservationByGuestPhoneReader</c>
/// applies to the stored <c>GuestPhone</c> column when comparing (ADR-029) —
/// no shared cross-context utility exists for this yet (audited before
/// writing this checkpoint; promoting it to one is its own future decision,
/// not assumed here), so both sides independently implement, document, and
/// unit-test the identical rule.
/// </summary>
public sealed class MetaWebhookMessageProcessor : IWhatsAppWebhookMessageProcessor
{
    private static readonly Regex NonDigits = new(@"\D+", RegexOptions.Compiled);

    private readonly IWhatsAppTenantRouteResolver _resolver;

    public MetaWebhookMessageProcessor(IWhatsAppTenantRouteResolver resolver) => _resolver = resolver;

    public async Task<IReadOnlyList<WebhookMessageProcessingOutcome>> ProcessAsync(
        ReadOnlyMemory<byte> rawBody, CancellationToken cancellationToken)
    {
        MetaWebhookEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<MetaWebhookEnvelope>(rawBody.Span);
        }
        catch (JsonException)
        {
            return [MalformedOutcome()];
        }

        if (envelope?.Entry is null || envelope.Entry.Count == 0)
            return [MalformedOutcome()];

        var outcomes = new List<WebhookMessageProcessingOutcome>();

        foreach (var change in envelope.Entry.SelectMany(entry => entry.Changes ?? []))
        {
            var value = change.Value;
            var messages = value?.Messages;

            // Status webhooks (or any change with no messages[]) are
            // silently ignored/deferred here — MetaWebhookStatusProcessor
            // handles those independently from the same raw body.
            if (messages is null || messages.Count == 0)
                continue;

            var phoneNumberId = value!.Metadata?.PhoneNumberId;
            if (string.IsNullOrWhiteSpace(phoneNumberId))
            {
                outcomes.AddRange(messages.Select(_ => MalformedOutcome()));
                continue;
            }

            var tenantId = await _resolver.ResolveTenantIdAsync(phoneNumberId, cancellationToken);

            outcomes.AddRange(messages.Select(message => BuildOutcome(tenantId, message)));
        }

        return outcomes;
    }

    private static WebhookMessageProcessingOutcome BuildOutcome(Guid? tenantId, MetaWebhookMessage message)
    {
        if (tenantId is null)
            return new WebhookMessageProcessingOutcome(WebhookMessageOutcomeKind.UnknownRoute, null, message.Id, null, null, null, null);

        if (string.IsNullOrWhiteSpace(message.Id) || string.IsNullOrWhiteSpace(message.From))
            return new WebhookMessageProcessingOutcome(WebhookMessageOutcomeKind.Malformed, tenantId, message.Id, null, null, null, null);

        if (!TryParseUnixTimestamp(message.Timestamp, out var occurredAtUtc))
            return new WebhookMessageProcessingOutcome(WebhookMessageOutcomeKind.Malformed, tenantId, message.Id, null, null, null, null);

        var normalizedPhone = NonDigits.Replace(message.From, string.Empty);
        if (normalizedPhone.Length == 0)
            return new WebhookMessageProcessingOutcome(WebhookMessageOutcomeKind.Malformed, tenantId, message.Id, null, null, null, occurredAtUtc);

        // CP1 is TEXT ONLY (mandate item 24) — every other type collapses
        // into Unsupported, its own type-specific payload never modeled.
        var isText = message.Type == "text" && message.Text?.Body is not null;

        return new WebhookMessageProcessingOutcome(
            WebhookMessageOutcomeKind.Accepted,
            tenantId,
            message.Id,
            normalizedPhone,
            isText ? InboundGuestMessageType.Text : InboundGuestMessageType.Unsupported,
            isText ? message.Text!.Body : null,
            occurredAtUtc);
    }

    /// <summary>Strict parse — never falls back to DateTimeOffset.UtcNow as a silent substitute for the provider's own timestamp (same discipline as MetaWebhookStatusProcessor).</summary>
    private static bool TryParseUnixTimestamp(string? raw, out DateTimeOffset occurredAtUtc)
    {
        occurredAtUtc = default;
        if (string.IsNullOrWhiteSpace(raw) || !long.TryParse(raw, out var unixSeconds))
            return false;

        try
        {
            occurredAtUtc = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static WebhookMessageProcessingOutcome MalformedOutcome() =>
        new(WebhookMessageOutcomeKind.Malformed, null, null, null, null, null, null);
}
