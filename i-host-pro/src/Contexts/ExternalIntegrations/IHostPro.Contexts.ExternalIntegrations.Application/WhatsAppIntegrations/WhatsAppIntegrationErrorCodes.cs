namespace IHostPro.Contexts.ExternalIntegrations.Application.WhatsAppIntegrations;

/// <summary>Stable error codes for the WhatsApp integration Enable/Disable commands — mirrors <c>AirbnbSyncErrorCodes</c>' convention.</summary>
public static class WhatsAppIntegrationErrorCodes
{
    /// <summary>No <c>WhatsAppIntegration</c> exists yet for the current tenant — <see cref="ConfigureWhatsAppIntegrationCommand"/> must run first.</summary>
    public const string WhatsAppIntegrationNotFound = "whatsapp_integration_not_found";

    /// <summary><c>Enable</c> was requested for an integration that is already enabled.</summary>
    public const string WhatsAppIntegrationAlreadyEnabled = "whatsapp_integration_already_enabled";

    /// <summary><c>Disable</c> was requested for an integration that is already disabled.</summary>
    public const string WhatsAppIntegrationAlreadyDisabled = "whatsapp_integration_already_disabled";

    /// <summary><c>Enable</c> was requested but <c>WabaId</c>/<c>PhoneNumberId</c>/one of the three secret references is still missing.</summary>
    public const string WhatsAppIntegrationConfigurationIncomplete = "whatsapp_integration_configuration_incomplete";

    /// <summary><c>Enable</c> was requested but at least one configured secret reference could not be resolved to a real value by <see cref="Application.IWhatsAppCredentialProvider"/>.</summary>
    public const string WhatsAppIntegrationCredentialsUnavailable = "whatsapp_integration_credentials_unavailable";
}
