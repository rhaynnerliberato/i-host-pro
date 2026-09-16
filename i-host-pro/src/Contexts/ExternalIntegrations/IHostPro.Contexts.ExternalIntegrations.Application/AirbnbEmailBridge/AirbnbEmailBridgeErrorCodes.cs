namespace IHostPro.Contexts.ExternalIntegrations.Application.AirbnbEmailBridge;

/// <summary>Stable error codes for the Airbnb Email Bridge Connect/Disconnect commands — mirrors <c>WhatsAppIntegrationErrorCodes</c>' convention.</summary>
public static class AirbnbEmailBridgeErrorCodes
{
    public const string ConnectionNotFound = "airbnb_email_connection_not_found";
    public const string UserCancelled = "airbnb_email_authentication_user_cancelled";
    public const string ConsentDenied = "airbnb_email_authentication_consent_denied";
    public const string AcquisitionFailed = "airbnb_email_authentication_acquisition_failed";
    public const string AccountIdentityUnavailable = "airbnb_email_authentication_account_identity_unavailable";

    // Automatic Publication Design + Safety gate.
    public const string AutoPublicationAlreadyEnabled = "airbnb_email_auto_publication_already_enabled";
    public const string AutoPublicationAlreadyDisabled = "airbnb_email_auto_publication_already_disabled";
    public const string AutoPublicationCutoffRequired = "airbnb_email_auto_publication_cutoff_required";
    public const string AutoPublicationMailboxNotConnected = "airbnb_email_auto_publication_mailbox_not_connected";
}
