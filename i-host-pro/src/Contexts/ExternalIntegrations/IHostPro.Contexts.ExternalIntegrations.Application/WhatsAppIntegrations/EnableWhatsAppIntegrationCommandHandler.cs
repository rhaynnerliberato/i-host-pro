using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;

namespace IHostPro.Contexts.ExternalIntegrations.Application.WhatsAppIntegrations;

/// <summary>
/// <c>IsEnabled</c> <see langword="false"/> → <see langword="true"/> (Real
/// Tenant WhatsApp Activation Readiness gate — SMALL_IMPLEMENTATION_GAP
/// plan). Mirrors <c>CancelReservationCommandHandler</c>'s own division of
/// responsibility: every precondition is checked HERE, against a precise
/// error code, before <see cref="Domain.WhatsAppIntegration.Enable"/> is
/// ever called — that method's own guard exceptions are a defensive
/// invariant, never the primary way this handler communicates a business
/// rule violation.
///
/// The credential preflight (<see cref="IWhatsAppCredentialProvider.GetSecretAsync"/>
/// for each of the three configured references) exists solely to stop
/// <c>IsEnabled</c> from becoming <see langword="true"/> when the secret
/// REFERENCES are present in this aggregate but the real VALUES cannot
/// actually be resolved (e.g. not yet provisioned in Secrets Manager) —
/// each resolved value is discarded immediately after the null/empty check;
/// never logged, returned, or persisted (mirrors
/// <see cref="IWhatsAppCredentialProvider"/>'s own documented contract).
/// </summary>
public sealed class EnableWhatsAppIntegrationCommandHandler : ICommandHandler<EnableWhatsAppIntegrationCommand, WhatsAppIntegrationResult>
{
    private static readonly Error NotFoundError = new(
        WhatsAppIntegrationErrorCodes.WhatsAppIntegrationNotFound, WhatsAppIntegrationErrorCodes.WhatsAppIntegrationNotFound);
    private static readonly Error AlreadyEnabledError = new(
        WhatsAppIntegrationErrorCodes.WhatsAppIntegrationAlreadyEnabled, WhatsAppIntegrationErrorCodes.WhatsAppIntegrationAlreadyEnabled);
    private static readonly Error ConfigurationIncompleteError = new(
        WhatsAppIntegrationErrorCodes.WhatsAppIntegrationConfigurationIncomplete, WhatsAppIntegrationErrorCodes.WhatsAppIntegrationConfigurationIncomplete);
    private static readonly Error CredentialsUnavailableError = new(
        WhatsAppIntegrationErrorCodes.WhatsAppIntegrationCredentialsUnavailable, WhatsAppIntegrationErrorCodes.WhatsAppIntegrationCredentialsUnavailable);

    private readonly IWhatsAppIntegrationRepository _repository;
    private readonly IWhatsAppCredentialProvider _credentialProvider;
    private readonly TimeProvider _timeProvider;

    public EnableWhatsAppIntegrationCommandHandler(
        IWhatsAppIntegrationRepository repository,
        IWhatsAppCredentialProvider credentialProvider,
        TimeProvider timeProvider)
    {
        _repository = repository;
        _credentialProvider = credentialProvider;
        _timeProvider = timeProvider;
    }

    public async ValueTask<Result<WhatsAppIntegrationResult>> Handle(EnableWhatsAppIntegrationCommand command, CancellationToken cancellationToken)
    {
        var integration = await _repository.GetForCurrentTenantAsync(cancellationToken);
        if (integration is null)
            return Result.Failure<WhatsAppIntegrationResult>(NotFoundError);

        if (integration.IsEnabled)
            return Result.Failure<WhatsAppIntegrationResult>(AlreadyEnabledError);

        if (string.IsNullOrWhiteSpace(integration.WabaId) ||
            string.IsNullOrWhiteSpace(integration.PhoneNumberId) ||
            string.IsNullOrWhiteSpace(integration.AccessTokenSecretReference) ||
            string.IsNullOrWhiteSpace(integration.AppSecretSecretReference) ||
            string.IsNullOrWhiteSpace(integration.VerifyTokenSecretReference))
        {
            return Result.Failure<WhatsAppIntegrationResult>(ConfigurationIncompleteError);
        }

        if (!await AllSecretsResolveAsync(integration, cancellationToken))
            return Result.Failure<WhatsAppIntegrationResult>(CredentialsUnavailableError);

        integration.Enable(_timeProvider.GetUtcNow());

        return Result.Success(ConfigureWhatsAppIntegrationCommandHandler.ToResult(integration));
    }

    private async Task<bool> AllSecretsResolveAsync(Domain.WhatsAppIntegration integration, CancellationToken cancellationToken)
    {
        string?[] references =
        [
            integration.AccessTokenSecretReference,
            integration.AppSecretSecretReference,
            integration.VerifyTokenSecretReference,
        ];

        foreach (var reference in references)
        {
            var resolved = await _credentialProvider.GetSecretAsync(reference!, cancellationToken);
            if (string.IsNullOrEmpty(resolved))
                return false;
        }

        return true;
    }
}
