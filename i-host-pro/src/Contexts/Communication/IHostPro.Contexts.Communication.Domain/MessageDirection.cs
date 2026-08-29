namespace IHostPro.Contexts.Communication.Domain;

/// <summary>
/// Fase 11, Checkpoint 1 (Inbound Conversation Foundation) — every
/// <see cref="Message"/> row prior to this checkpoint is <see cref="Outbound"/>
/// (backfilled by migration); <see cref="Inbound"/> is used exclusively by
/// <c>Message.CreateInbound</c>.
/// </summary>
public enum MessageDirection
{
    Outbound,
    Inbound,
}
