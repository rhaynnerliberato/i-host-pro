namespace IHostPro.Contexts.ExternalIntegrations.Application.AirbnbEmailBridge;

/// <summary>
/// Fetches one page of a mailbox's incremental message synchronization.
/// Provider-neutral by name (this Bounded Context's own architecture rule
/// forbids "Graph" vocabulary outside Infrastructure — see
/// <c>ExternalIntegrationsDependencyTests.Meta_Named_Types_Only_Exist_In_The_Infrastructure_Meta_Namespace</c>,
/// whose forbidden-substring list also happens to catch the mailbox
/// provider's own product name).
/// </summary>
public interface IAirbnbEmailMessageSource
{
    /// <param name="deltaOrNextLink">
    /// The opaque cursor to resume from (a prior page's <c>NextLink</c> or a
    /// sync state's persisted <c>DeltaLink</c>), or <c>null</c> to start a
    /// fresh synchronization of <paramref name="mailFolderId"/>.
    /// </param>
    Task<AirbnbEmailDeltaFetchOutcome> GetDeltaPageAsync(
        string accessToken, string mailFolderId, string? deltaOrNextLink, CancellationToken cancellationToken);

    /// <summary>
    /// Fetches the full subject and body of one specific message, by id -
    /// deliberately a separate, targeted call rather than widening the delta
    /// page's own <c>$select</c> (which would fetch every message's full
    /// content on every poll, most of which are never candidates for any
    /// parser). Callers are expected to only invoke this for messages that
    /// already look like a candidate (e.g. sender domain) from the cheap
    /// summary fields the delta page already returns, OR to reconstruct an
    /// already-receipted message for a manual retry via its persisted
    /// <c>GraphMessageId</c> (Airbnb Email Operational Exception Resolution
    /// gate) - in both cases the SAME single request returns both fields the
    /// parser needs, never a second round trip for whichever one the delta
    /// page's summary already happened to carry. Returns <c>null</c> on any
    /// failure, including the source message no longer existing - the caller
    /// treats this the same as "cannot parse this message right now", never
    /// as a reason to fail the whole polling run or mutate a retry's receipt.
    /// </summary>
    Task<AirbnbEmailMessageContent?> GetMessageContentAsync(string accessToken, string messageId, CancellationToken cancellationToken);
}
