namespace IHostPro.Contexts.Communication.Domain;

/// <summary>
/// Mirrors Documento 06 §9 ("Máquina de Estados da Comunicação"). CP1
/// modeled the synchronous-send subset: <c>Criada</c> → <c>NaFila</c> →
/// <c>Enviando</c> → <c>Enviada</c>, alternate <c>Enviando</c> →
/// <c>Falhou</c> — <c>Recebida</c>/<c>Lida</c> and <c>Reprocessando</c> were
/// deliberately deferred (no webhook, no retry policy existed yet).
///
/// Fase 9, Checkpoint 2.3.3 (ADR-022 item 14) adds <see cref="Delivered"/>
/// (Documento 06's <c>Recebida</c>) and <see cref="Read"/> (<c>Lida</c>),
/// driven by <c>WhatsAppMessageStatusChanged</c> via
/// <see cref="Message.ApplyProviderStatus"/> — never by the synchronous send
/// path. <c>Reprocessando</c> remains deliberately out of scope (no retry
/// policy this checkpoint either).
///
/// Stored via <c>HasConversion&lt;string&gt;()</c> (see <c>MessageConfiguration</c>)
/// — adding these two values needed no schema/column-type migration, only
/// new valid string values within the existing <c>varchar(20)</c> column.
/// </summary>
public enum MessageStatus
{
    Created,
    Queued,
    Sending,
    Sent,
    Delivered,
    Read,
    Failed,

    /// <summary>
    /// Fase 11, Checkpoint 1 (Inbound Conversation Foundation) — the sole
    /// status for a <see cref="MessageDirection.Inbound"/> row, set once at
    /// <c>Message.CreateInbound</c> and never transitioned further this
    /// checkpoint (no read-receipt/reply-tracking modeled yet). Documento
    /// 06 §9's own <c>Recebida</c>.
    /// </summary>
    Received,
}
