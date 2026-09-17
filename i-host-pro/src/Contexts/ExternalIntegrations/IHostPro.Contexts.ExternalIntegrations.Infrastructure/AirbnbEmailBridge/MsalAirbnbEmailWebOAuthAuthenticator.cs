using System.Security.Cryptography;
using System.Text;
using IHostPro.Contexts.ExternalIntegrations.Application.AirbnbEmailBridge;
using IHostPro.Contexts.ExternalIntegrations.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Client;

namespace IHostPro.Contexts.ExternalIntegrations.Infrastructure.AirbnbEmailBridge;

/// <summary>
/// MSAL.NET confidential-client implementation of
/// <see cref="IAirbnbEmailWebOAuthAuthenticator"/> — a SEPARATE class from
/// <see cref="MsalAirbnbEmailAuthenticator"/> (Web OAuth architecture gate,
/// item 17/27: "avoid one giant conditional authenticator"), because the
/// underlying MSAL client type genuinely differs: <see cref="IPublicClientApplication"/>
/// has no supported way to redeem an authorization code obtained on a
/// SEPARATE, later HTTP request — only <see cref="IConfidentialClientApplication.AcquireTokenByAuthorizationCode"/>
/// does. Both classes share the same <see cref="IAirbnbEmailTokenCacheStore"/>
/// and the same <see cref="AirbnbEmailMailboxConnection.Connect"/> domain
/// method — no duplicated persistence.
///
/// The <c>/authorize</c> leg is built by hand (plain query-string
/// construction), not via MSAL — there is no need for an MSAL client object
/// just to compose a URL, and this keeps full, transparent control over the
/// exact state/PKCE parameters. MSAL is used ONLY for the <c>/token</c> leg
/// (<see cref="CompleteAsync"/>), which is the part that actually needs to
/// populate the shared token cache correctly.
/// </summary>
public sealed class MsalAirbnbEmailWebOAuthAuthenticator : IAirbnbEmailWebOAuthAuthenticator
{
    private const int CodeVerifierBytes = 32; // 43 base64url chars — within the RFC 7636 43-128 char range.
    private const int StateBytes = 32; // 256 bits of entropy.

    private readonly IAirbnbEmailMailboxConnectionRepository _repository;
    private readonly IAirbnbEmailTokenCacheStore _tokenCacheStore;
    private readonly IAirbnbEmailUnitOfWork _unitOfWork;
    private readonly IOptions<AirbnbEmailBridgeOptions> _options;
    private readonly IOAuthTransactionSecretProtector _pkceProtector;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<MsalAirbnbEmailWebOAuthAuthenticator> _logger;

    public MsalAirbnbEmailWebOAuthAuthenticator(
        IAirbnbEmailMailboxConnectionRepository repository,
        IAirbnbEmailTokenCacheStore tokenCacheStore,
        IAirbnbEmailUnitOfWork unitOfWork,
        IOptions<AirbnbEmailBridgeOptions> options,
        IOAuthTransactionSecretProtector pkceProtector,
        TimeProvider timeProvider,
        ILogger<MsalAirbnbEmailWebOAuthAuthenticator> logger)
    {
        _repository = repository;
        _tokenCacheStore = tokenCacheStore;
        _unitOfWork = unitOfWork;
        _options = options;
        _pkceProtector = pkceProtector;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public AirbnbEmailWebOAuthAuthorizationRequest? BuildAuthorizationRequest()
    {
        var options = _options.Value;
        if (string.IsNullOrWhiteSpace(options.ClientId) ||
            string.IsNullOrWhiteSpace(options.ClientSecret) ||
            string.IsNullOrWhiteSpace(options.WebRedirectUri) ||
            string.IsNullOrWhiteSpace(options.WebFrontendReturnUrl))
        {
            _logger.LogError(
                "Airbnb Email Bridge Web OAuth is not configured (ClientId/ClientSecret/WebRedirectUri/WebFrontendReturnUrl) - cannot start authorization.");
            return null;
        }

        var codeVerifier = GenerateUrlSafeRandomString(CodeVerifierBytes);
        var codeChallenge = ComputeCodeChallenge(codeVerifier);
        var state = GenerateUrlSafeRandomString(StateBytes);

        var query = string.Join('&', new[]
        {
            $"client_id={Uri.EscapeDataString(options.ClientId)}",
            "response_type=code",
            $"redirect_uri={Uri.EscapeDataString(options.WebRedirectUri)}",
            "response_mode=query",
            $"scope={Uri.EscapeDataString(string.Join(' ', options.Scopes))}",
            $"state={Uri.EscapeDataString(state)}",
            $"code_challenge={Uri.EscapeDataString(codeChallenge)}",
            "code_challenge_method=S256",
        });
        var authorizationUrl = $"{options.Authority}/oauth2/v2.0/authorize?{query}";

        var protectedVerifier = _pkceProtector.Protect(Encoding.ASCII.GetBytes(codeVerifier));

        return new AirbnbEmailWebOAuthAuthorizationRequest(authorizationUrl, state, protectedVerifier);
    }

    public async Task<AirbnbEmailWebOAuthCallbackOutcome> CompleteAsync(
        Guid tenantId, string authorizationCode, byte[] protectedPkceVerifier, CancellationToken cancellationToken)
    {
        var options = _options.Value;
        var codeVerifier = Encoding.ASCII.GetString(_pkceProtector.Unprotect(protectedPkceVerifier));

        await EnsureConnectionRowExistsAsync(tenantId, cancellationToken);

        var app = ConfidentialClientApplicationBuilder.Create(options.ClientId)
            .WithClientSecret(options.ClientSecret)
            .WithAuthority(options.Authority)
            .WithRedirectUri(options.WebRedirectUri)
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
            authResult = await app.AcquireTokenByAuthorizationCode(options.Scopes, authorizationCode)
                .WithPkceCodeVerifier(codeVerifier)
                .ExecuteAsync(cancellationToken);
        }
        catch (MsalServiceException ex) when (string.Equals(ex.ErrorCode, "access_denied", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Airbnb Email Bridge Web OAuth consent denied for tenant {TenantId}.", tenantId);
            return AirbnbEmailWebOAuthCallbackOutcome.Failure(AirbnbEmailWebOAuthCallbackFailureReason.ConsentDenied);
        }
        catch (MsalException ex)
        {
            _logger.LogError(ex, "Airbnb Email Bridge Web OAuth token exchange failed for tenant {TenantId}.", tenantId);
            return AirbnbEmailWebOAuthCallbackOutcome.Failure(AirbnbEmailWebOAuthCallbackFailureReason.ExchangeFailed);
        }

        if (authResult.Account is null)
        {
            _logger.LogError("Airbnb Email Bridge Web OAuth succeeded but MSAL returned no account for tenant {TenantId}.", tenantId);
            return AirbnbEmailWebOAuthCallbackOutcome.Failure(AirbnbEmailWebOAuthCallbackFailureReason.AccountIdentityUnavailable);
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

        return AirbnbEmailWebOAuthCallbackOutcome.Success();
    }

    /// <summary>Mirrors <see cref="MsalAirbnbEmailAuthenticator.EnsureConnectionRowExistsAsync"/> exactly — same MSAL cache-changed-callback-can-fire-mid-flow reason.</summary>
    private async Task EnsureConnectionRowExistsAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        await _unitOfWork.ExecuteAsync(async () =>
        {
            var existing = await _repository.GetForCurrentTenantAsync(cancellationToken);
            if (existing is null)
                _repository.Add(AirbnbEmailMailboxConnection.Create(Guid.NewGuid(), tenantId, _timeProvider.GetUtcNow()));

            return true;
        }, cancellationToken);
    }

    private static string GenerateUrlSafeRandomString(int byteCount) =>
        Base64UrlEncode(RandomNumberGenerator.GetBytes(byteCount));

    private static string ComputeCodeChallenge(string codeVerifier) =>
        Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier)));

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
