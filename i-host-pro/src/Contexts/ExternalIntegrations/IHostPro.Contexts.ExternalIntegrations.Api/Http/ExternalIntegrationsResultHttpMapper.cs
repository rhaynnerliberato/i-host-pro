using IHostPro.BuildingBlocks.Domain;
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
