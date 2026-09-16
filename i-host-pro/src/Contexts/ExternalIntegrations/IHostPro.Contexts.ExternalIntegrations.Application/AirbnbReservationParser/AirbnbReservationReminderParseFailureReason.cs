namespace IHostPro.Contexts.ExternalIntegrations.Application.AirbnbReservationParser;

public enum AirbnbReservationReminderParseFailureReason
{
    /// <summary>The message does not contain the reservation-card structural landmarks (property type + Check-in + Checkout) this parser recognizes — it is not a reservation-reminder email, or is a format this parser was never designed against.</summary>
    UnsupportedTemplate,

    /// <summary>The template was recognized, but one specific required field's label/position was not found.</summary>
    MissingRequiredField,

    /// <summary>The "Código de confirmação" label was found, but the value immediately following it does not match the expected shape (9-11 uppercase alphanumeric characters) — the label's context does not support treating it as a real confirmation code.</summary>
    InvalidConfirmationCodeContext,
}
