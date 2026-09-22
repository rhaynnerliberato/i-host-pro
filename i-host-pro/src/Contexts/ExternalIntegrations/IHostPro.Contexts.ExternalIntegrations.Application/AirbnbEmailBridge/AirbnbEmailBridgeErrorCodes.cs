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

    // Web OAuth architecture gate.
    public const string WebOAuthNotConfigured = "airbnb_email_web_oauth_not_configured";

    // Airbnb Email Operational Exception Resolution gate.
    public const string ReceiptNotFound = "airbnb_email_receipt_not_found";
    public const string ReceiptNotRetryable = "airbnb_email_receipt_not_retryable";
    public const string ReceiptMailboxNotConnected = "airbnb_email_receipt_mailbox_not_connected";
    public const string ReceiptSourceMessageUnavailable = "airbnb_email_receipt_source_message_unavailable";
    public const string ReceiptRetryConflict = "airbnb_email_receipt_retry_conflict";
}
