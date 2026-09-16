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
}
