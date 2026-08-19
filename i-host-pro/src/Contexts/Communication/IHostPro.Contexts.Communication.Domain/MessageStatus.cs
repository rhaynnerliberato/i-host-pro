namespace IHostPro.Contexts.Communication.Domain;

/// <summary>
/// Mirrors Documento 06 §9 ("Máquina de Estados da Comunicação")'s MVP
/// subset for this checkpoint: <c>Criada</c> → <c>NaFila</c> → <c>Enviando</c>
/// → <c>Enviada</c>, alternate <c>Enviando</c> → <c>Falhou</c>. Documento
/// 06's <c>Recebida</c>/<c>Lida</c> (delivery/read receipts, real WhatsApp
/// webhook) and <c>Reprocessando</c> (retry policy) are out of scope for
/// Checkpoint 1 (fake connector, no webhook, no retry) — deliberately not
/// modeled here rather than added speculatively.
/// </summary>
public enum MessageStatus
{
    Created,
    Queued,
    Sending,
    Sent,
    Failed,
}
