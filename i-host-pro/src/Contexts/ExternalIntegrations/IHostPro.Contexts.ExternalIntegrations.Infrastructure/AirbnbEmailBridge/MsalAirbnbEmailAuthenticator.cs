using IHostPro.Contexts.ExternalIntegrations.Application.AirbnbEmailBridge;
using IHostPro.Contexts.ExternalIntegrations.Domain;
using IHostPro.Contexts.ExternalIntegrations.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Client;

namespace IHostPro.Contexts.ExternalIntegrations.Infrastructure.AirbnbEmailBridge;

/// <summary>
/// MSAL.NET (Microsoft.Identity.Client)-backed <see cref="IAirbnbEmailAuthenticator"/>
/// for the LOCAL interactive flow. <see cref="ConnectInteractiveAsync"/> is
/// always a public client + Authorization Code/PKCE via
/// <see cref="PublicClientApplicationBuilder"/> — no client secret (Fase 9
/// review §1-2), unchanged by the Web OAuth architecture gate (item 25:
/// "preserve current local interactive flow"). It runs
/// <see cref="IPublicClientApplication.AcquireTokenInteractive"/> with a
/// system-browser loopback listener on <c>http://localhost</c>; this blocks
/// the calling request until the developer completes sign-in in their own
/// browser, so it is only ever meant to be invoked against a local Api
/// instance (Fase 9 review §21).
///
/// <see cref="AcquireTokenSilentAsync"/> is different: it is shared by BOTH
/// the local flow above and the Worker's background polling, and now also
/// by mailboxes connected through the separate confidential-client Web OAuth
/// flow (<c>MsalAirbnbEmailWebOAuthAuthenticator</c>) — see that method's own
/// remarks for the deterministic public-vs-confidential client selection.
///
/// Sequencing note: <see cref="IAirbnbEmailTokenCacheStore.SaveAsync"/>
/// requires an existing <see cref="AirbnbEmailMailboxConnection"/> row (it
/// never creates one — see that interface's own contract). Since MSAL's
/// cache-changed callback can fire DURING <c>AcquireTokenInteractive</c>,
/// this class ensures a connection row already exists (creating and saving
/// an empty one on first connect) BEFORE starting the interactive flow, then
/// fills in the account identity via <see cref="AirbnbEmailMailboxConnection.Connect"/>
/// only after authentication succeeds. The token cache blob itself is never
/// touched here directly — only through the store, which is what encrypts it
/// (<see cref="ITokenCacheProtector"/>).
/// </summary>
public sealed class MsalAirbnbEmailAuthenticator : IAirbnbEmailAuthenticator
{
    private readonly IAirbnbEmailMailboxConnectionRepository _repository;
    private readonly IAirbnbEmailTokenCacheStore _tokenCacheStore;
    private readonly IAirbnbEmailUnitOfWork _unitOfWork;
    private readonly IOptions<AirbnbEmailBridgeOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<MsalAirbnbEmailAuthenticator> _logger;

    public MsalAirbnbEmailAuthenticator(
        IAirbnbEmailMailboxConnectionRepository repository,
        IAirbnbEmailTokenCacheStore tokenCacheStore,
        IAirbnbEmailUnitOfWork unitOfWork,
        IOptions<AirbnbEmailBridgeOptions> options,
        TimeProvider timeProvider,
        ILogger<MsalAirbnbEmailAuthenticator> logger)
    {
        _repository = repository;
        _tokenCacheStore = tokenCacheStore;
        _unitOfWork = unitOfWork;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<AirbnbEmailAuthenticationOutcome> ConnectInteractiveAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var options = _options.Value;
        if (string.IsNullOrWhiteSpace(options.ClientId))
        {
            _logger.LogError("ExternalIntegrations:AirbnbEmailBridge:ClientId is not configured - cannot start Microsoft authentication.");
            return AirbnbEmailAuthenticationOutcome.Failure(AirbnbEmailAuthenticationFailureReason.AcquisitionFailed);
        }

        await EnsureConnectionRowExistsAsync(tenantId, cancellationToken);

        var app = PublicClientApplicationBuilder.Create(options.ClientId)
            .WithAuthority(options.Authority)
            .WithRedirectUri(options.RedirectUri)
            .Build();

        app.UserTokenCache.SetBeforeAccessAsync(async args =>
        {
            var cached = await _tokenCacheStore.LoadAsync(tenantId, cancellationToken);
            if (cached is not null)
                args.TokenCache.DeserializeMsalV3(cached);
        });

        app.UserTokenCache.SetAfterAccessAsync(async args =>
        {
            if (args.HasStateChanged)
                await _tokenCacheStore.SaveAsync(tenantId, args.TokenCache.SerializeMsalV3(), cancellationToken);
        });

        AuthenticationResult authResult;
        try
        {
            authResult = await app.AcquireTokenInteractive(options.Scopes).ExecuteAsync(cancellationToken);
        }
        catch (MsalClientException ex) when (ex.ErrorCode == MsalError.AuthenticationCanceledError)
        {
            _logger.LogInformation("Airbnb Email Bridge connect attempt cancelled by the user for tenant {TenantId}.", tenantId);
            return AirbnbEmailAuthenticationOutcome.Failure(AirbnbEmailAuthenticationFailureReason.UserCancelled);
        }
        catch (MsalServiceException ex) when (string.Equals(ex.ErrorCode, "access_denied", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Airbnb Email Bridge consent denied for tenant {TenantId}.", tenantId);
            return AirbnbEmailAuthenticationOutcome.Failure(AirbnbEmailAuthenticationFailureReason.ConsentDenied);
        }
        catch (MsalException ex)
        {
            _logger.LogError(ex, "Airbnb Email Bridge Microsoft authentication failed for tenant {TenantId}.", tenantId);
            return AirbnbEmailAuthenticationOutcome.Failure(AirbnbEmailAuthenticationFailureReason.AcquisitionFailed);
        }

        if (authResult.Account is null)
        {
            _logger.LogError("Airbnb Email Bridge authentication succeeded but MSAL returned no account for tenant {TenantId}.", tenantId);
            return AirbnbEmailAuthenticationOutcome.Failure(AirbnbEmailAuthenticationFailureReason.AccountIdentityUnavailable);
        }

        var homeAccountId = authResult.Account.HomeAccountId.Identifier;
        var accountTenantId = authResult.Account.HomeAccountId.TenantId;
        var mailboxAddress = authResult.Account.Username;
        var grantedScopes = string.Join(' ', authResult.Scopes);
        var connectedAtUtc = _timeProvider.GetUtcNow();

        await _unitOfWork.ExecuteAsync(async () =>
        {
            var connection = await _repository.GetForCurrentTenantAsync(cancellationToken)
                ?? throw new InvalidOperationException($"Airbnb Email Bridge connection row for tenant {tenantId:D} disappeared mid-flow.");
            connection.Connect(homeAccountId, accountTenantId, mailboxAddress, grantedScopes, connectedAtUtc);
            return true;
        }, cancellationToken);

        return AirbnbEmailAuthenticationOutcome.Success(homeAccountId, accountTenantId, mailboxAddress, grantedScopes);
    }

    public async Task<AirbnbEmailSilentAcquisitionOutcome> AcquireTokenSilentAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var options = _options.Value;
        if (string.IsNullOrWhiteSpace(options.ClientId))
        {
            _logger.LogError("ExternalIntegrations:AirbnbEmailBridge:ClientId is not configured - cannot acquire a token silently.");
            return AirbnbEmailSilentAcquisitionOutcome.Failure(reauthorizationRequired: false);
        }

        var homeAccountId = await _unitOfWork.ExecuteAsync(
            async () => (await _repository.GetForCurrentTenantAsync(cancellationToken))?.HomeAccountId,
            cancellationToken);
        if (homeAccountId is null)
        {
            _logger.LogWarning("Airbnb Email Bridge silent acquisition requested for tenant {TenantId} with no connected mailbox.", tenantId);
            return AirbnbEmailSilentAcquisitionOutcome.Failure(reauthorizationRequired: true);
        }

        // Web OAuth architecture gate, items 23-29: a token cache populated
        // through the confidential-client Web OAuth flow may need the client
        // credential to refresh (Microsoft's own web-app refresh model).
        // Deterministic rule (the smallest one that avoids inventing an
        // AuthenticationMode field, per the gate's own item 28 — see
        // MsalAirbnbEmailWebOAuthAuthenticator's remarks for the parallel
        // confidential-client construction): a configured ClientSecret always
        // switches silent acquisition to IConfidentialClientApplication, for
        // EVERY cached account regardless of which flow connected it. Absent
        // a secret, this is exactly the pre-existing public-client path. Both
        // implement the common IClientApplicationBase surface used below, so
        // only the few lines constructing `app` differ.
        IClientApplicationBase app = string.IsNullOrWhiteSpace(options.ClientSecret)
            ? PublicClientApplicationBuilder.Create(options.ClientId)
                .WithAuthority(options.Authority)
                .WithRedirectUri(options.RedirectUri)
                .Build()
            : ConfidentialClientApplicationBuilder.Create(options.ClientId)
                .WithClientSecret(options.ClientSecret)
                .WithAuthority(options.Authority)
                .Build();

        app.UserTokenCache.SetBeforeAccessAsync(async args =>
        {
            var cached = await _tokenCacheStore.LoadAsync(tenantId, cancellationToken);
            if (cached is not null)
                args.TokenCache.DeserializeMsalV3(cached);
        });

        app.UserTokenCache.SetAfterAccessAsync(async args =>
        {
            if (args.HasStateChanged)
                await _tokenCacheStore.SaveAsync(tenantId, args.TokenCache.SerializeMsalV3(), cancellationToken);
        });

        var accounts = await app.GetAccountsAsync();
        var account = accounts.FirstOrDefault(a => a.HomeAccountId.Identifier == homeAccountId);
        if (account is null)
        {
            _logger.LogWarning(
                "Airbnb Email Bridge silent acquisition for tenant {TenantId}: no matching account in the restored token cache.", tenantId);
            return AirbnbEmailSilentAcquisitionOutcome.Failure(reauthorizationRequired: true);
        }

        try
        {
            var result = await app.AcquireTokenSilent(options.Scopes, account).ExecuteAsync(cancellationToken);
            return AirbnbEmailSilentAcquisitionOutcome.Success(result.AccessToken);
        }
        catch (MsalUiRequiredException)
        {
            _logger.LogWarning("Airbnb Email Bridge silent acquisition for tenant {TenantId} requires interactive reauthorization.", tenantId);
            return AirbnbEmailSilentAcquisitionOutcome.Failure(reauthorizationRequired: true);
        }
        catch (MsalException ex)
        {
            _logger.LogError(ex, "Airbnb Email Bridge silent token acquisition failed transiently for tenant {TenantId}.", tenantId);
            return AirbnbEmailSilentAcquisitionOutcome.Failure(reauthorizationRequired: false);
        }
    }

    private async Task EnsureConnectionRowExistsAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        // Committed immediately (its own tenant-scoped transaction), ahead of
        // AcquireTokenInteractive: MSAL's cache-changed callback can fire
        // DURING that call, and IAirbnbEmailTokenCacheStore.SaveAsync needs
        // this row to already be queryable (under RLS) when that happens.
        await _unitOfWork.ExecuteAsync(async () =>
        {
            var existing = await _repository.GetForCurrentTenantAsync(cancellationToken);
            if (existing is null)
                _repository.Add(AirbnbEmailMailboxConnection.Create(Guid.NewGuid(), tenantId, _timeProvider.GetUtcNow()));

            return true;
        }, cancellationToken);
    }
}
