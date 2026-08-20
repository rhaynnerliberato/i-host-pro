using IHostPro.BuildingBlocks.Domain;

namespace IHostPro.Contexts.Communication.Domain;

/// <summary>
/// A single outbound guest communication, triggered by a Reservation
/// lifecycle event (Fase 9, Checkpoint 1 — choreography, no Workflow
/// involved: Documento 06 §9's Message state machine, Communication's own
/// aggregate). Communication is the sole owner of this lifecycle (CP1
/// mandate §27) — a Connector only ever reports a technical outcome; this
/// aggregate decides which transition that outcome permits.
///
/// <see cref="DestinationMasked"/> deliberately never stores the guest's
/// full phone number — only the last four digits, in the same format
/// WhatsApp/carriers commonly mask it (e.g. <c>"*******1234"</c>). The full
/// number is read from <c>IReservationGuestContactReader</c> at send time,
/// passed directly to the connector, and never persisted here — proportional
/// to the CP1 mandate's own instruction (§24) not to persist PII beyond
/// operational necessity for a first checkpoint whose only requirement is a
/// provably-observable lifecycle, not a searchable contact history.
/// </summary>
public sealed class Message : AggregateRoot<Guid>, ITenantOwned
{
    public Guid TenantId { get; private set; }
    public Guid ReservationId { get; private set; }
    public string Channel { get; private set; } = null!;
    public string TemplateKey { get; private set; } = null!;
    public string? DestinationMasked { get; private set; }
    public string RenderedContent { get; private set; } = null!;
    public string IdempotencyKey { get; private set; } = null!;
    public MessageStatus Status { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset? SentAtUtc { get; private set; }
    public DateTimeOffset? DeliveredAtUtc { get; private set; }
    public DateTimeOffset? ReadAtUtc { get; private set; }
    public DateTimeOffset? FailedAtUtc { get; private set; }
    public string? FailureReason { get; private set; }
    public string? ProviderMessageId { get; private set; }

    private Message()
    {
        // EF Core materialization.
    }

    private Message(
        Guid id, Guid tenantId, Guid reservationId, string channel, string templateKey,
        string? destinationMasked, string renderedContent, string idempotencyKey, DateTimeOffset createdAtUtc)
        : base(id)
    {
        TenantId = tenantId;
        ReservationId = reservationId;
        Channel = channel;
        TemplateKey = templateKey;
        DestinationMasked = destinationMasked;
        RenderedContent = renderedContent;
        IdempotencyKey = idempotencyKey;
        Status = MessageStatus.Created;
        CreatedAtUtc = createdAtUtc;
    }

    public static Message Create(
        Guid id, Guid tenantId, Guid reservationId, string channel, string templateKey,
        string? destinationMasked, string renderedContent, string idempotencyKey, DateTimeOffset createdAtUtc)
    {
        if (string.IsNullOrWhiteSpace(channel))
            throw new ArgumentException("Channel cannot be empty.", nameof(channel));
        if (string.IsNullOrWhiteSpace(templateKey))
            throw new ArgumentException("Template key cannot be empty.", nameof(templateKey));
        if (string.IsNullOrWhiteSpace(renderedContent))
            throw new ArgumentException("Rendered content cannot be empty.", nameof(renderedContent));
        if (string.IsNullOrWhiteSpace(idempotencyKey))
            throw new ArgumentException("Idempotency key cannot be empty.", nameof(idempotencyKey));

        return new Message(
            id, tenantId, reservationId, channel, templateKey, destinationMasked, renderedContent, idempotencyKey, createdAtUtc);
    }

    /// <summary>Criada → NaFila. The state actually persisted by the first <c>SaveChangesAsync</c> (CP1 mandate §34).</summary>
    public void MarkQueued()
    {
        EnsureStatus(MessageStatus.Created, nameof(MarkQueued));
        Status = MessageStatus.Queued;
    }

    /// <summary>
    /// NaFila → Enviando. Never persisted as its own committed row on its
    /// own — the connector call happens between this and the terminal
    /// transition, and only the terminal outcome (<see cref="MarkSent"/>/
    /// <see cref="MarkFailed"/>) is saved, avoiding a false DB+HTTP
    /// atomicity (CP1 mandate §34).
    /// </summary>
    public void MarkSending()
    {
        EnsureStatus(MessageStatus.Queued, nameof(MarkSending));
        Status = MessageStatus.Sending;
    }

    /// <summary>
    /// Enviando → Enviada. <paramref name="providerMessageId"/> is the
    /// provider's own opaque message identifier (e.g. a WhatsApp
    /// <c>wamid</c>) — <see langword="null"/> for a connector that reports no
    /// id (the CP1 fake connector) — never parsed/validated here (Fase 9,
    /// Checkpoint 2.2 — provider-neutral, mandate §25/§26).
    /// </summary>
    public void MarkSent(DateTimeOffset sentAtUtc, string? providerMessageId = null)
    {
        EnsureStatus(MessageStatus.Sending, nameof(MarkSent));
        Status = MessageStatus.Sent;
        SentAtUtc = sentAtUtc;
        ProviderMessageId = providerMessageId;
    }

    /// <summary>
    /// Enviando → Falhou (the connector rejected/failed the send), OR
    /// NaFila → Falhou directly (no destination was ever available — e.g.
    /// the Reservation has no guest phone on file — so no connector call
    /// was ever attempted; still a real, auditable outcome, never silently
    /// dropped). <paramref name="reason"/> is an operational code/short
    /// message — never a raw exception message or stack trace as business
    /// data (CP1 mandate §48).
    /// </summary>
    public void MarkFailed(string reason, DateTimeOffset failedAtUtc)
    {
        if (Status is not (MessageStatus.Queued or MessageStatus.Sending))
            throw new InvalidOperationException(
                $"Cannot {nameof(MarkFailed)} a Message in status {Status} — expected {MessageStatus.Queued} or {MessageStatus.Sending}.");
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("Failure reason cannot be empty.", nameof(reason));

        Status = MessageStatus.Failed;
        FailureReason = reason;
        FailedAtUtc = failedAtUtc;
    }

    /// <summary>
    /// Applies a WhatsApp provider status change (Fase 9, Checkpoint 2.3.3,
    /// ADR-022 item 14) — idempotently, never throwing for a duplicate or
    /// out-of-order report (mandate §19/§20): only a genuinely forward
    /// transition mutates state; the caller inspects the returned
    /// <see cref="ProviderStatusApplicationResult"/> to decide which audit
    /// event to emit, never a try/catch.
    ///
    /// Only ever called after this <see cref="Message"/> was found by
    /// <c>ProviderMessageId</c> — a lookup that structurally can never match
    /// a row in <see cref="MessageStatus.Created"/>/<see cref="MessageStatus.Queued"/>/
    /// <see cref="MessageStatus.Sending"/>, since <see cref="ProviderMessageId"/>
    /// is only ever set together with <see cref="MarkSent"/> (Checkpoint
    /// 2.3.3 mandate §22 — confirmed closed by this very invariant, no race
    /// is possible at the lookup level). The guard below is defensive, not
    /// expected to ever trigger.
    ///
    /// Approved transition matrix (mandate §17-21): <c>Sent → Delivered</c>,
    /// <c>Sent → Read</c>, <c>Sent → Failed</c>, <c>Delivered → Read</c>,
    /// <c>Delivered → Failed</c> are Forward. Same-status repeats are
    /// Duplicate. Everything else — including anything reported once already
    /// <see cref="MessageStatus.Failed"/>, and <c>Read → Failed</c>
    /// specifically (<see cref="MessageStatus.Read"/> is terminal for Failed
    /// purposes, corrected in ExternalIntegrations' own Checkpoint 2.3.2.1) —
    /// is Regression. Never resolved by blind ordinal comparison alone
    /// (mandate §8/§20): the Failed branch is explicit, separate logic.
    /// </summary>
    public ProviderStatusApplicationResult ApplyProviderStatus(
        WhatsAppProviderStatus status, DateTimeOffset occurredAtUtc, int? providerErrorCode = null)
    {
        if (Status is MessageStatus.Created or MessageStatus.Queued or MessageStatus.Sending)
            throw new InvalidOperationException(
                $"Cannot apply provider status {status} to a Message in status {Status} — " +
                $"ProviderMessageId (the only way this Message could have been looked up) is only ever set together with {nameof(MarkSent)}.");

        var classification = Classify(Status, status);
        if (classification != ProviderStatusApplicationResult.Applied)
            return classification;

        switch (status)
        {
            case WhatsAppProviderStatus.Delivered:
                Status = MessageStatus.Delivered;
                DeliveredAtUtc = occurredAtUtc;
                break;
            case WhatsAppProviderStatus.Read:
                Status = MessageStatus.Read;
                ReadAtUtc = occurredAtUtc;
                break;
            case WhatsAppProviderStatus.Failed:
                Status = MessageStatus.Failed;
                FailedAtUtc = occurredAtUtc;
                // Reuses FailureReason (never a new column, mandate §25) —
                // already documented as "an operational code/short message,
                // never a raw exception" for the synchronous MarkFailed
                // path; a provider error code fits the same description.
                FailureReason = providerErrorCode is { } code ? $"provider_error_{code}" : "provider_reported_failure";
                break;
            default:
                // Sent can structurally never be a Forward transition here:
                // Status is already guaranteed >= Sent by the guard above, so
                // incoming Sent is always either Duplicate (Status == Sent)
                // or Regression (Status is Delivered/Read/Failed) — see
                // Classify below. Never reached.
                throw new InvalidOperationException($"Unreachable: {status} was classified Applied but has no forward-transition handling.");
        }

        return classification;
    }

    private static readonly Dictionary<MessageStatus, int> ProviderStatusRank = new()
    {
        [MessageStatus.Sent] = 1,
        [MessageStatus.Delivered] = 2,
        [MessageStatus.Read] = 3,
    };

    private static ProviderStatusApplicationResult Classify(MessageStatus current, WhatsAppProviderStatus incoming)
    {
        var incomingStatus = ToMessageStatus(incoming);

        if (current == incomingStatus)
            return ProviderStatusApplicationResult.Duplicate;

        if (current == MessageStatus.Failed)
            return ProviderStatusApplicationResult.Regression; // Failed is terminal — nothing advances past it.

        if (incomingStatus == MessageStatus.Failed)
        {
            // Read is terminal for Failed purposes; Sent/Delivered are not —
            // mirrors ExternalIntegrations.Domain.WhatsAppStatusTransitionClassifier's
            // own corrected (Checkpoint 2.3.2.1) rule exactly, small
            // duplication accepted (mandate §34).
            return current == MessageStatus.Read
                ? ProviderStatusApplicationResult.Regression
                : ProviderStatusApplicationResult.Applied;
        }

        return ProviderStatusRank[incomingStatus] > ProviderStatusRank[current]
            ? ProviderStatusApplicationResult.Applied
            : ProviderStatusApplicationResult.Regression;
    }

    private static MessageStatus ToMessageStatus(WhatsAppProviderStatus status) => status switch
    {
        WhatsAppProviderStatus.Sent => MessageStatus.Sent,
        WhatsAppProviderStatus.Delivered => MessageStatus.Delivered,
        WhatsAppProviderStatus.Read => MessageStatus.Read,
        WhatsAppProviderStatus.Failed => MessageStatus.Failed,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };

    private void EnsureStatus(MessageStatus expected, string operation)
    {
        if (Status != expected)
            throw new InvalidOperationException(
                $"Cannot {operation} a Message in status {Status} — expected {expected}.");
    }
}
