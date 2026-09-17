using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.Contexts.ExternalIntegrations.Application.AirbnbEmailBridge;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IHostPro.Contexts.ExternalIntegrations.Infrastructure.AirbnbEmailBridge;

/// <inheritdoc cref="IAirbnbEmailWebOAuthCallbackProcessor"/>
/// <remarks>
/// The ONLY class in this Bounded Context that calls
/// <see cref="ITenantContext.SetTenant"/> outside the JWT bearer
/// authentication pipeline — deliberate, and only after
/// <see cref="IAirbnbEmailOAuthTransactionRepository.ConsumeByStateHashAsync"/>
/// has already returned a trusted result (Web OAuth architecture gate, items
/// 13-18). Never sets the tenant from anything the browser/Microsoft sent
/// directly.
/// </remarks>
public sealed class AirbnbEmailWebOAuthCallbackProcessor : IAirbnbEmailWebOAuthCallbackProcessor
{
    private readonly IAirbnbEmailOAuthTransactionRepository _transactionRepository;
    private readonly IAirbnbEmailWebOAuthAuthenticator _authenticator;
    private readonly ITenantContext _tenantContext;
    private readonly IOptions<AirbnbEmailBridgeOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AirbnbEmailWebOAuthCallbackProcessor> _logger;

    public AirbnbEmailWebOAuthCallbackProcessor(
        IAirbnbEmailOAuthTransactionRepository transactionRepository,
        IAirbnbEmailWebOAuthAuthenticator authenticator,
        ITenantContext tenantContext,
        IOptions<AirbnbEmailBridgeOptions> options,
        TimeProvider timeProvider,
        ILogger<AirbnbEmailWebOAuthCallbackProcessor> logger)
    {
        _transactionRepository = transactionRepository;
        _authenticator = authenticator;
        _tenantContext = tenantContext;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<AirbnbEmailWebOAuthCallbackReport> ProcessAsync(
        string? state, string? authorizationCode, string? microsoftError, CancellationToken cancellationToken)
    {
        var frontendReturnUrl = _options.Value.WebFrontendReturnUrl;
        if (string.IsNullOrWhiteSpace(frontendReturnUrl))
        {
            _logger.LogError("Airbnb Email Bridge Web OAuth callback received but WebFrontendReturnUrl is not configured.");
            return new AirbnbEmailWebOAuthCallbackReport(AirbnbEmailWebOAuthCallbackResult.NotConfigured, null);
        }

        // Microsoft's own redirect carries an error instead of a code when
        // the user cancels or denies consent - never a raw code+error pair.
        if (!string.IsNullOrEmpty(microsoftError))
        {
            _logger.LogInformation("Airbnb Email Bridge Web OAuth callback received a Microsoft error: {MicrosoftError}.", microsoftError);
            return new AirbnbEmailWebOAuthCallbackReport(AirbnbEmailWebOAuthCallbackResult.UserCancelledOrConsentDenied, frontendReturnUrl);
        }

        if (string.IsNullOrEmpty(state) || string.IsNullOrEmpty(authorizationCode))
        {
            _logger.LogWarning("Airbnb Email Bridge Web OAuth callback is missing state or code.");
            return new AirbnbEmailWebOAuthCallbackReport(AirbnbEmailWebOAuthCallbackResult.InvalidOrExpiredState, frontendReturnUrl);
        }

        // Deliberately BEFORE any tenant context exists - see this class's
        // own remarks and IAirbnbEmailOAuthTransactionRepository's contract.
        var stateHash = AirbnbEmailOAuthStateHasher.Hash(state);
        var consumption = await _transactionRepository.ConsumeByStateHashAsync(stateHash, _timeProvider.GetUtcNow(), cancellationToken);
        if (consumption is null)
        {
            _logger.LogWarning("Airbnb Email Bridge Web OAuth callback presented an invalid, expired, or already-consumed state.");
            return new AirbnbEmailWebOAuthCallbackReport(AirbnbEmailWebOAuthCallbackResult.InvalidOrExpiredState, frontendReturnUrl);
        }

        // ONLY NOW is the tenant trusted - recovered exclusively from the
        // just-consumed transaction row, never from any callback input.
        _tenantContext.SetTenant(consumption.TenantId);

        var outcome = await _authenticator.CompleteAsync(
            consumption.TenantId, authorizationCode, consumption.ProtectedPkceVerifier, cancellationToken);

        var result = outcome.IsSuccess
            ? AirbnbEmailWebOAuthCallbackResult.Success
            : outcome.FailureReason switch
            {
                AirbnbEmailWebOAuthCallbackFailureReason.ConsentDenied => AirbnbEmailWebOAuthCallbackResult.UserCancelledOrConsentDenied,
                _ => AirbnbEmailWebOAuthCallbackResult.ExchangeFailed,
            };

        return new AirbnbEmailWebOAuthCallbackReport(result, frontendReturnUrl);
    }
}
