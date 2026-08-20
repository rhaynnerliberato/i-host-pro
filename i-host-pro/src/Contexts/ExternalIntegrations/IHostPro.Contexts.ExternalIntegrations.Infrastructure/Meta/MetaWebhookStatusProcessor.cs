using System.Text.Json;
using IHostPro.Contexts.ExternalIntegrations.Application.WhatsAppTenantRoutes;
using IHostPro.Contexts.ExternalIntegrations.Domain;

namespace IHostPro.Contexts.ExternalIntegrations.Infrastructure.Meta;

/// <summary>
/// Meta-specific implementation of <see cref="IWhatsAppWebhookStatusProcessor"/>
/// (Fase 9, Checkpoint 2.3.2). All Meta envelope parsing lives here, entirely
/// confined to <c>Infrastructure.Meta</c> — the Api layer only ever sees the
/// provider-neutral <see cref="WebhookStatusProcessingOutcome"/> (ADR-022
/// item 16).
///
/// Deliberately foundation-only: resolves tenant per status entry and
/// normalizes the status — never mutates <c>Communication.Message</c>, never
/// persists a dedup receipt, never publishes anything (Checkpoint 2.3.3).
/// </summary>
public sealed class MetaWebhookStatusProcessor : IWhatsAppWebhookStatusProcessor
{
    private readonly IWhatsAppTenantRouteResolver _resolver;

    public MetaWebhookStatusProcessor(IWhatsAppTenantRouteResolver resolver) => _resolver = resolver;

    public async Task<IReadOnlyList<WebhookStatusProcessingOutcome>> ProcessAsync(
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

        var outcomes = new List<WebhookStatusProcessingOutcome>();

        foreach (var change in envelope.Entry.SelectMany(entry => entry.Changes ?? []))
        {
            var value = change.Value;
            var statuses = value?.Statuses;

            // Inbound-message webhooks (or any change with no statuses[])
            // are silently ignored/deferred — never a business defect
            // (mandate §17), never counted as malformed.
            if (statuses is null || statuses.Count == 0)
                continue;

            var phoneNumberId = value!.Metadata?.PhoneNumberId;
            if (string.IsNullOrWhiteSpace(phoneNumberId))
            {
                outcomes.AddRange(statuses.Select(_ => MalformedOutcome()));
                continue;
            }

            var tenantId = await _resolver.ResolveTenantIdAsync(phoneNumberId, cancellationToken);

            outcomes.AddRange(statuses.Select(status => BuildOutcome(tenantId, status)));
        }

        return outcomes;
    }

    private static WebhookStatusProcessingOutcome BuildOutcome(Guid? tenantId, MetaWebhookStatus status)
    {
        if (tenantId is null)
            return new WebhookStatusProcessingOutcome(WebhookStatusOutcomeKind.UnknownRoute, null, status.Id, null, null, null);

        if (string.IsNullOrWhiteSpace(status.Id) || string.IsNullOrWhiteSpace(status.Status))
            return new WebhookStatusProcessingOutcome(WebhookStatusOutcomeKind.Malformed, tenantId, status.Id, null, null, null);

        if (!TryParseUnixTimestamp(status.Timestamp, out var occurredAtUtc))
            return new WebhookStatusProcessingOutcome(WebhookStatusOutcomeKind.Malformed, tenantId, status.Id, null, null, null);

        var normalizedStatus = MapStatus(status.Status);
        if (normalizedStatus is null)
            return new WebhookStatusProcessingOutcome(WebhookStatusOutcomeKind.Malformed, tenantId, status.Id, null, occurredAtUtc, null);

        var errorCode = normalizedStatus == ProviderMessageStatus.Failed
            ? status.Errors?.FirstOrDefault()?.Code
            : null;

        return new WebhookStatusProcessingOutcome(
            WebhookStatusOutcomeKind.Accepted, tenantId, status.Id, normalizedStatus, occurredAtUtc, errorCode);
    }

    /// <summary>Includes Meta's own <c>played</c> (voice-message-only, deferred — mandate §18) and any future/unrecognized status string.</summary>
    private static ProviderMessageStatus? MapStatus(string raw) => raw switch
    {
        "sent" => ProviderMessageStatus.Sent,
        "delivered" => ProviderMessageStatus.Delivered,
        "read" => ProviderMessageStatus.Read,
        "failed" => ProviderMessageStatus.Failed,
        _ => null,
    };

    /// <summary>Strict parse — never falls back to DateTimeOffset.UtcNow as a silent substitute for the provider's own timestamp (mandate §21).</summary>
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

    private static WebhookStatusProcessingOutcome MalformedOutcome() =>
        new(WebhookStatusOutcomeKind.Malformed, null, null, null, null, null);
}
