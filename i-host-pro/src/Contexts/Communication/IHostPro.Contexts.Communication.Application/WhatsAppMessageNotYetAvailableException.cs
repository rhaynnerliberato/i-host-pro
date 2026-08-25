namespace IHostPro.Contexts.Communication.Application;

/// <summary>
/// Thrown by <see cref="WhatsAppMessageStatusCommunicationProcessor"/> when
/// a <c>WhatsAppMessageStatusChanged</c> event carries a valid
/// <c>TenantId</c>/<c>ProviderMessageId</c> pair but no matching
/// <see cref="Domain.Message"/> is found yet — a potentially transient
/// condition caused by the CP2.2 outbound/webhook race (see the
/// processor's own doc comment: CP2.2's send path only commits
/// <c>Sent</c>+<c>ProviderMessageId</c> after the Meta HTTP round trip
/// completes, so a webhook can genuinely arrive before that commit lands),
/// never a permanent classification by itself.
///
/// Deliberately its own type, never the generic <see cref="InvalidOperationException"/>
/// this checkpoint originally used (Checkpoint 2.3.3.1 corrective mandate):
/// Wolverine's own bounded-retry policy
/// (<c>WhatsAppMessageStatusChangedHandler.Configure</c>) is scoped to
/// exactly this exception type — an unrelated bug elsewhere in the
/// processor throwing a generic <see cref="InvalidOperationException"/>
/// must never accidentally receive the same retry treatment.
/// </summary>
public sealed class WhatsAppMessageNotYetAvailableException : Exception
{
    public WhatsAppMessageNotYetAvailableException(string message) : base(message)
    {
    }
}
