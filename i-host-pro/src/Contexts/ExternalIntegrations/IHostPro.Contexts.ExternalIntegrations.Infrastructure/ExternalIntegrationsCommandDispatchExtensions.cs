using FluentValidation;
using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.BuildingBlocks.Infrastructure.Persistence;
using IHostPro.Contexts.ExternalIntegrations.Application;
using IHostPro.Contexts.ExternalIntegrations.Application.WhatsAppIntegrations;
using IHostPro.Contexts.ExternalIntegrations.Application.WhatsAppTemplateMappings;
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
        services.AddScoped<IValidator<ConfigureWhatsAppTemplateMappingCommand>, ConfigureWhatsAppTemplateMappingCommandValidator>();

        // Validation runs first for every command — safe as a single open
        // generic, no tenant/transaction side effects to collide with.
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));

        // IWhatsAppIntegrationRepository/IWhatsAppTemplateMappingRepository are
        // registered by AddExternalIntegrationsModule itself (Fase 9,
        // Checkpoint 2.2) — every call site calls that before this method, and
        // MetaWhatsAppMessagingProvider needs both without depending on this
        // Mediator-specific dispatch registration.

        // Fase 9, Checkpoint 2.1.1 — registered BEFORE TenantTransactionBehavior
        // so it wraps AROUND it (this codebase's own convention: first-registered
        // runs outermost, see the ValidationBehavior comment above). This is what
        // lets the audit log "Success" only after TenantTransactionBehavior's own
        // commit has genuinely completed inside the next() call this behavior
        // awaits — never before it, and never by changing the transaction
        // boundary itself (CP2.1.1 mandate §11/§16).
        services.AddScoped<
            IPipelineBehavior<ConfigureWhatsAppIntegrationCommand, Result<WhatsAppIntegrationResult>>,
            AuditConfigureWhatsAppIntegrationBehavior>();

        services.AddScoped<
            IPipelineBehavior<ConfigureWhatsAppIntegrationCommand, Result<WhatsAppIntegrationResult>>,
            TenantTransactionBehavior<ConfigureWhatsAppIntegrationCommand, Result<WhatsAppIntegrationResult>, ExternalIntegrationsDbContext>>();
        services.AddScoped<
            IPipelineBehavior<GetWhatsAppIntegrationQuery, Result<WhatsAppIntegrationResult>>,
            TenantTransactionBehavior<GetWhatsAppIntegrationQuery, Result<WhatsAppIntegrationResult>, ExternalIntegrationsDbContext>>();

        // Fase 9, Checkpoint 2.2 — WhatsAppTemplateMapping admin commands/
        // queries, same TenantTransactionBehavior wiring as WhatsAppIntegration
        // above.
        //
        // Fase 12, Checkpoint 4 (Security/Secrets/LGPD Hardening) — the "no
        // audit behavior" note that used to be here is corrected:
        // ActorUserId was always carried by the command but never read
        // anywhere. AuditConfigureWhatsAppTemplateMappingBehavior closes that
        // gap by cloning AuditConfigureWhatsAppIntegrationBehavior's own
        // pattern exactly (same registration order — outermost, wrapping
        // TenantTransactionBehavior below — for the same reason: "Success"
        // must log only after the inner transaction genuinely commits).
        services.AddScoped<
            IPipelineBehavior<ConfigureWhatsAppTemplateMappingCommand, Result<WhatsAppTemplateMappingResult>>,
            AuditConfigureWhatsAppTemplateMappingBehavior>();

        services.AddScoped<
            IPipelineBehavior<ConfigureWhatsAppTemplateMappingCommand, Result<WhatsAppTemplateMappingResult>>,
            TenantTransactionBehavior<ConfigureWhatsAppTemplateMappingCommand, Result<WhatsAppTemplateMappingResult>, ExternalIntegrationsDbContext>>();
        services.AddScoped<
            IPipelineBehavior<GetWhatsAppTemplateMappingQuery, Result<WhatsAppTemplateMappingResult>>,
            TenantTransactionBehavior<GetWhatsAppTemplateMappingQuery, Result<WhatsAppTemplateMappingResult>, ExternalIntegrationsDbContext>>();

        return services;
    }
}
