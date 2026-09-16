namespace IHostPro.Contexts.ExternalIntegrations.Application.AirbnbEmailBridge;

/// <summary>One incremental synchronization attempt for one tenant's connected mailbox — see <c>AirbnbEmailDeltaSyncRunner</c> for the full consistency model.</summary>
public interface IAirbnbEmailDeltaSyncRunner
{
    Task RunAsync(Guid tenantId, string mailFolderId, CancellationToken cancellationToken);
}
