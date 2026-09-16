using IHostPro.Contexts.ExternalIntegrations.Application.AirbnbEmailBridge;
using IHostPro.Contexts.ExternalIntegrations.Domain;

namespace IHostPro.Contexts.ExternalIntegrations.Tests.Unit.Application.AirbnbEmailBridge;

/// <summary>
/// On success, mirrors what <c>MsalAirbnbEmailAuthenticator</c> does against
/// the real repository (calls <see cref="AirbnbEmailMailboxConnection.Connect"/>
/// on the tenant's row) — so handler tests exercise the same post-condition
/// the real authenticator establishes, without any real MSAL/network call.
/// </summary>
internal sealed class FakeAirbnbEmailAuthenticator : IAirbnbEmailAuthenticator
{
    private readonly FakeAirbnbEmailMailboxConnectionRepository _repository;
    private readonly AirbnbEmailAuthenticationOutcome _outcome;

    public AirbnbEmailSilentAcquisitionOutcome SilentOutcome { get; set; } = AirbnbEmailSilentAcquisitionOutcome.Success("fake-access-token");
    public int SilentAcquisitionCallCount { get; private set; }

    private FakeAirbnbEmailAuthenticator(FakeAirbnbEmailMailboxConnectionRepository repository, AirbnbEmailAuthenticationOutcome outcome)
    {
        _repository = repository;
        _outcome = outcome;
    }

    public static FakeAirbnbEmailAuthenticator Succeeding(FakeAirbnbEmailMailboxConnectionRepository repository) =>
        new(repository, AirbnbEmailAuthenticationOutcome.Success("home-account-1", "entra-tenant-1", "guest@hotmail.com", "Mail.Read"));

    public static FakeAirbnbEmailAuthenticator Failing(
        FakeAirbnbEmailMailboxConnectionRepository repository, AirbnbEmailAuthenticationFailureReason reason) =>
        new(repository, AirbnbEmailAuthenticationOutcome.Failure(reason));

    public Task<AirbnbEmailAuthenticationOutcome> ConnectInteractiveAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        if (_outcome.IsSuccess)
        {
            var connection = _repository.Current ?? AirbnbEmailMailboxConnection.Create(Guid.NewGuid(), tenantId, DateTimeOffset.UtcNow);
            connection.Connect(_outcome.HomeAccountId!, _outcome.AccountTenantId, _outcome.MailboxAddress, _outcome.GrantedScopes, DateTimeOffset.UtcNow);
            _repository.Add(connection);
        }

        return Task.FromResult(_outcome);
    }

    public Task<AirbnbEmailSilentAcquisitionOutcome> AcquireTokenSilentAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        SilentAcquisitionCallCount++;
        return Task.FromResult(SilentOutcome);
    }
}
