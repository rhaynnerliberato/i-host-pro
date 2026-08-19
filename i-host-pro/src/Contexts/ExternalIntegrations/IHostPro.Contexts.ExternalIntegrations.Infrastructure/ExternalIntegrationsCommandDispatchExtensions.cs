using FluentValidation;
using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.BuildingBlocks.Infrastructure.Persistence;
using IHostPro.Contexts.ExternalIntegrations.Application;
using IHostPro.Contexts.ExternalIntegrations.Application.WhatsAppIntegrations;
using IHostPro.Contexts.ExternalIntegrations.Infrastructure.Persistence;
using Mediator;
using Microsoft.Extensions.DependencyInjection;

namespace IHostPro.Contexts.ExternalIntegrations.Infrastructure;

/// <summary>
/// Single composition-root entry point for dispatching External
/// Integrations' Commands/Queries — mirrors
/// <c>ConfigurationCommandDispatchExtensions</c> exactly. Called ONLY from
/// <c>IHostPro.Api</c>'s composition root (the administrative configuration
/// API is the only consumer this checkpoint).
/// </summary>
public static class ExternalIntegrationsCommandDispatchExtensions
{
    public static IServiceCollection AddExternalIntegrationsCommandDispatch(this IServiceCollection services)
    {
        services.AddExternalIntegrationsApplicationMediator();

        services.AddScoped<IValidator<ConfigureWhatsAppIntegrationCommand>, ConfigureWhatsAppIntegrationCommandValidator>();

        // Validation runs first for every command — safe as a single open
        // generic, no tenant/transaction side effects to collide with.
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));

        services.AddScoped<IWhatsAppIntegrationRepository, WhatsAppIntegrationRepository>();

        services.AddScoped<
            IPipelineBehavior<ConfigureWhatsAppIntegrationCommand, Result<WhatsAppIntegrationResult>>,
            TenantTransactionBehavior<ConfigureWhatsAppIntegrationCommand, Result<WhatsAppIntegrationResult>, ExternalIntegrationsDbContext>>();
        services.AddScoped<
            IPipelineBehavior<GetWhatsAppIntegrationQuery, Result<WhatsAppIntegrationResult>>,
            TenantTransactionBehavior<GetWhatsAppIntegrationQuery, Result<WhatsAppIntegrationResult>, ExternalIntegrationsDbContext>>();

        return services;
    }
}
