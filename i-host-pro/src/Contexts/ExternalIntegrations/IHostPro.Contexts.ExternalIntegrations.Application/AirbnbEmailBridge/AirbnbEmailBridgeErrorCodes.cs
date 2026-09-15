namespace IHostPro.Contexts.ExternalIntegrations.Application.AirbnbEmailBridge;

/// <summary>Stable error codes for the Airbnb Email Bridge Connect/Disconnect commands — mirrors <c>WhatsAppIntegrationErrorCodes</c>' convention.</summary>
public static class AirbnbEmailBridgeErrorCodes
{
    public const string ConnectionNotFound = "airbnb_email_connection_not_found";
    public const string UserCancelled = "airbnb_email_authentication_user_cancelled";
    public const string ConsentDenied = "airbnb_email_authentication_consent_denied";
    public const string AcquisitionFailed = "airbnb_email_authentication_acquisition_failed";
    public const string AccountIdentityUnavailable = "airbnb_email_authentication_account_identity_unavailable";
}
