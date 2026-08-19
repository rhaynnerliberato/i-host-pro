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
    public DateTimeOffset? FailedAtUtc { get; private set; }
    public string? FailureReason { get; private set; }

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

    /// <summary>Enviando → Enviada.</summary>
    public void MarkSent(DateTimeOffset sentAtUtc)
    {
        EnsureStatus(MessageStatus.Sending, nameof(MarkSent));
        Status = MessageStatus.Sent;
        SentAtUtc = sentAtUtc;
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

    private void EnsureStatus(MessageStatus expected, string operation)
    {
        if (Status != expected)
            throw new InvalidOperationException(
                $"Cannot {operation} a Message in status {Status} — expected {expected}.");
    }
}
