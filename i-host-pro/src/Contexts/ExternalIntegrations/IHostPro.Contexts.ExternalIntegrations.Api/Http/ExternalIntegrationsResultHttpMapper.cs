using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.ExternalIntegrations.Application.AirbnbEmailBridge;
using IHostPro.Contexts.ExternalIntegrations.Application.AirbnbListingTitleMappings;
using IHostPro.Contexts.ExternalIntegrations.Application.WhatsAppIntegrations;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace IHostPro.Contexts.ExternalIntegrations.Api.Http;

/// <summary>
/// The single, centralized place a failed <see cref="Result"/>/<see cref="Result{TValue}"/>
/// from any External Integrations command/query becomes an HTTP response —
/// mirrors <c>TemplateResultHttpMapper</c>'s structure exactly. Introduced by
/// the Real Tenant WhatsApp Activation Readiness gate
/// (SMALL_IMPLEMENTATION_GAP plan): <c>ConfigureWhatsAppIntegrationCommand</c>/
/// <c>ConfigureWhatsAppTemplateMappingCommand</c> never failed before Enable/
/// Disable existed, so this Bounded Context never needed one until now.
/// </summary>
public static class ExternalIntegrationsResultHttpMapper
{
    public static IActionResult ToActionResult(Error error)
    {
        var (status, title) = error.Code switch
        {
            WhatsAppIntegrationErrorCodes.WhatsAppIntegrationNotFound => (StatusCodes.Status404NotFound, WhatsAppIntegrationErrorCodes.WhatsAppIntegrationNotFound),
            WhatsAppIntegrationErrorCodes.WhatsAppIntegrationAlreadyEnabled => (StatusCodes.Status409Conflict, WhatsAppIntegrationErrorCodes.WhatsAppIntegrationAlreadyEnabled),
            WhatsAppIntegrationErrorCodes.WhatsAppIntegrationAlreadyDisabled => (StatusCodes.Status409Conflict, WhatsAppIntegrationErrorCodes.WhatsAppIntegrationAlreadyDisabled),
            WhatsAppIntegrationErrorCodes.WhatsAppIntegrationConfigurationIncomplete => (StatusCodes.Status409Conflict, WhatsAppIntegrationErrorCodes.WhatsAppIntegrationConfigurationIncomplete),
            WhatsAppIntegrationErrorCodes.WhatsAppIntegrationCredentialsUnavailable => (StatusCodes.Status409Conflict, WhatsAppIntegrationErrorCodes.WhatsAppIntegrationCredentialsUnavailable),
            AirbnbEmailBridgeErrorCodes.ConnectionNotFound => (StatusCodes.Status404NotFound, AirbnbEmailBridgeErrorCodes.ConnectionNotFound),
            AirbnbEmailBridgeErrorCodes.UserCancelled => (StatusCodes.Status409Conflict, AirbnbEmailBridgeErrorCodes.UserCancelled),
            AirbnbEmailBridgeErrorCodes.ConsentDenied => (StatusCodes.Status409Conflict, AirbnbEmailBridgeErrorCodes.ConsentDenied),
            AirbnbEmailBridgeErrorCodes.AcquisitionFailed => (StatusCodes.Status502BadGateway, AirbnbEmailBridgeErrorCodes.AcquisitionFailed),
            AirbnbEmailBridgeErrorCodes.AccountIdentityUnavailable => (StatusCodes.Status502BadGateway, AirbnbEmailBridgeErrorCodes.AccountIdentityUnavailable),
            AirbnbEmailBridgeErrorCodes.AutoPublicationAlreadyEnabled => (StatusCodes.Status409Conflict, AirbnbEmailBridgeErrorCodes.AutoPublicationAlreadyEnabled),
            AirbnbEmailBridgeErrorCodes.AutoPublicationAlreadyDisabled => (StatusCodes.Status409Conflict, AirbnbEmailBridgeErrorCodes.AutoPublicationAlreadyDisabled),
            AirbnbEmailBridgeErrorCodes.AutoPublicationCutoffRequired => (StatusCodes.Status400BadRequest, AirbnbEmailBridgeErrorCodes.AutoPublicationCutoffRequired),
            AirbnbEmailBridgeErrorCodes.AutoPublicationMailboxNotConnected => (StatusCodes.Status409Conflict, AirbnbEmailBridgeErrorCodes.AutoPublicationMailboxNotConnected),
            AirbnbEmailBridgeErrorCodes.WebOAuthNotConfigured => (StatusCodes.Status409Conflict, AirbnbEmailBridgeErrorCodes.WebOAuthNotConfigured),
            AirbnbEmailBridgeErrorCodes.ReceiptNotFound => (StatusCodes.Status404NotFound, AirbnbEmailBridgeErrorCodes.ReceiptNotFound),
            AirbnbEmailBridgeErrorCodes.ReceiptNotRetryable => (StatusCodes.Status409Conflict, AirbnbEmailBridgeErrorCodes.ReceiptNotRetryable),
            AirbnbEmailBridgeErrorCodes.ReceiptMailboxNotConnected => (StatusCodes.Status409Conflict, AirbnbEmailBridgeErrorCodes.ReceiptMailboxNotConnected),
            AirbnbEmailBridgeErrorCodes.ReceiptSourceMessageUnavailable => (StatusCodes.Status409Conflict, AirbnbEmailBridgeErrorCodes.ReceiptSourceMessageUnavailable),
            AirbnbEmailBridgeErrorCodes.ReceiptRetryConflict => (StatusCodes.Status409Conflict, AirbnbEmailBridgeErrorCodes.ReceiptRetryConflict),
            AirbnbListingTitleMappingErrorCodes.DuplicateListingTitle => (StatusCodes.Status409Conflict, AirbnbListingTitleMappingErrorCodes.DuplicateListingTitle),
            _ => (StatusCodes.Status400BadRequest, "validation_error"),
        };

        var problem = new ProblemDetails { Status = status, Title = title };

        if (title == "validation_error")
        {
            var codes = error.Code.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            problem.Extensions["codes"] = codes;
        }

        return new ObjectResult(problem) { StatusCode = status };
    }
}
