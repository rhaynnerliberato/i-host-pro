using FluentValidation;
using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.BuildingBlocks.Infrastructure.Persistence;
using IHostPro.Contexts.ExternalIntegrations.Application;
using IHostPro.Contexts.ExternalIntegrations.Application.AirbnbEmailBridge;
using IHostPro.Contexts.ExternalIntegrations.Application.AirbnbListingTitleMappings;
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

        // Real Tenant WhatsApp Activation Readiness gate (SMALL_IMPLEMENTATION_GAP
        // plan) — Enable/Disable commands, same audit-outermost/TenantTransactionBehavior
        // wiring as ConfigureWhatsAppIntegrationCommand above.
        services.AddScoped<
            IPipelineBehavior<EnableWhatsAppIntegrationCommand, Result<WhatsAppIntegrationResult>>,
            AuditEnableWhatsAppIntegrationBehavior>();
        services.AddScoped<
            IPipelineBehavior<EnableWhatsAppIntegrationCommand, Result<WhatsAppIntegrationResult>>,
            TenantTransactionBehavior<EnableWhatsAppIntegrationCommand, Result<WhatsAppIntegrationResult>, ExternalIntegrationsDbContext>>();

        services.AddScoped<
            IPipelineBehavior<DisableWhatsAppIntegrationCommand, Result<WhatsAppIntegrationResult>>,
            AuditDisableWhatsAppIntegrationBehavior>();
        services.AddScoped<
            IPipelineBehavior<DisableWhatsAppIntegrationCommand, Result<WhatsAppIntegrationResult>>,
            TenantTransactionBehavior<DisableWhatsAppIntegrationCommand, Result<WhatsAppIntegrationResult>, ExternalIntegrationsDbContext>>();

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

        // Airbnb Reservation Email Parser gate - Mapping + DRY_RUN
        // Orchestration - same TenantTransactionBehavior wiring as every
        // other command/query above (required for the RLS-protected
        // AirbnbListingTitleMappingRepository to have app.tenant_id set).
        // No audit behavior yet - not requested for this gate, unlike the
        // Airbnb Email Bridge Connect/Disconnect commands' own (Fase 12 CP4
        // LGPD-driven) audit trail.
        services.AddScoped<
            IPipelineBehavior<CreateAirbnbListingTitleMappingCommand, Result<AirbnbListingTitleMappingResult>>,
            TenantTransactionBehavior<CreateAirbnbListingTitleMappingCommand, Result<AirbnbListingTitleMappingResult>, ExternalIntegrationsDbContext>>();
        services.AddScoped<
            IPipelineBehavior<ListAirbnbListingTitleMappingsQuery, Result<IReadOnlyList<AirbnbListingTitleMappingResult>>>,
            TenantTransactionBehavior<ListAirbnbListingTitleMappingsQuery, Result<IReadOnlyList<AirbnbListingTitleMappingResult>>, ExternalIntegrationsDbContext>>();

        // Airbnb Email Bridge Audit Behavior DI Hardening gate - registers
        // the pre-existing AuditConnectAirbnbEmailMailboxBehavior/
        // AuditDisconnectAirbnbEmailMailboxBehavior classes, which existed
        // fully written but were never wired here. AUDIT ONLY - deliberately
        // NOT the audit-outermost/TenantTransactionBehavior pair used
        // everywhere else in this file: MsalAirbnbEmailAuthenticator (the
        // RLS/tenant-context gate) now owns its own tenant-scoped transaction
        // for Connect, and ConnectAirbnbEmailMailboxCommandHandler/
        // DisconnectAirbnbEmailMailboxCommandHandler fix their own remaining
        // reads the same way (see IAirbnbEmailUnitOfWork.ExecuteAsync calls
        // in those handlers) - wrapping either command in an ADDITIONAL
        // ambient TenantTransactionBehavior would nest a second transaction
        // on the same DbContext and throw NestedUnitOfWorkException.
        services.AddScoped<
            IPipelineBehavior<ConnectAirbnbEmailMailboxCommand, Result<AirbnbEmailMailboxConnectionResult>>,
            AuditConnectAirbnbEmailMailboxBehavior>();
        services.AddScoped<
            IPipelineBehavior<DisconnectAirbnbEmailMailboxCommand, Result<AirbnbEmailMailboxConnectionResult>>,
            AuditDisconnectAirbnbEmailMailboxBehavior>();

        // Automatic Publication Design + Safety gate - Enable/Disable/Get
        // commands for the tenant opt-in flag, same audit-outermost/
        // TenantTransactionBehavior wiring as WhatsAppIntegration's own
        // Enable/Disable above.
        services.AddScoped<
            IPipelineBehavior<EnableAirbnbAutoPublicationCommand, Result<AirbnbAutoPublicationStatusResult>>,
            AuditEnableAirbnbAutoPublicationBehavior>();
        services.AddScoped<
            IPipelineBehavior<EnableAirbnbAutoPublicationCommand, Result<AirbnbAutoPublicationStatusResult>>,
            TenantTransactionBehavior<EnableAirbnbAutoPublicationCommand, Result<AirbnbAutoPublicationStatusResult>, ExternalIntegrationsDbContext>>();

        services.AddScoped<
            IPipelineBehavior<DisableAirbnbAutoPublicationCommand, Result<AirbnbAutoPublicationStatusResult>>,
            AuditDisableAirbnbAutoPublicationBehavior>();
        services.AddScoped<
            IPipelineBehavior<DisableAirbnbAutoPublicationCommand, Result<AirbnbAutoPublicationStatusResult>>,
            TenantTransactionBehavior<DisableAirbnbAutoPublicationCommand, Result<AirbnbAutoPublicationStatusResult>, ExternalIntegrationsDbContext>>();

        services.AddScoped<
            IPipelineBehavior<GetAirbnbAutoPublicationStatusQuery, Result<AirbnbAutoPublicationStatusResult>>,
            TenantTransactionBehavior<GetAirbnbAutoPublicationStatusQuery, Result<AirbnbAutoPublicationStatusResult>, ExternalIntegrationsDbContext>>();

        // Airbnb Email Bridge Minimal Operations/UX gate - read-only status
        // and processing-summary queries, same TenantTransactionBehavior
        // wiring as GetAirbnbAutoPublicationStatusQuery above (plain
        // single-repository reads, no authenticator call inside them, so no
        // nested-transaction concern here).
        services.AddScoped<
            IPipelineBehavior<GetAirbnbEmailBridgeStatusQuery, Result<AirbnbEmailBridgeStatusResult>>,
            TenantTransactionBehavior<GetAirbnbEmailBridgeStatusQuery, Result<AirbnbEmailBridgeStatusResult>, ExternalIntegrationsDbContext>>();
        services.AddScoped<
            IPipelineBehavior<GetAirbnbEmailProcessingSummaryQuery, Result<AirbnbEmailProcessingSummaryResult>>,
            TenantTransactionBehavior<GetAirbnbEmailProcessingSummaryQuery, Result<AirbnbEmailProcessingSummaryResult>, ExternalIntegrationsDbContext>>();

        // Web OAuth architecture gate - oauth/start is a NORMAL authenticated
        // command (unlike Connect/Disconnect): nothing inside it manages its
        // own nested transaction, so the standard audit-outermost/
        // TenantTransactionBehavior pair applies with no nesting concern.
        // oauth/callback deliberately bypasses this Mediator dispatch
        // entirely - see IAirbnbEmailWebOAuthCallbackProcessor's own remarks.
        services.AddScoped<
            IPipelineBehavior<StartAirbnbEmailWebOAuthCommand, Result<AirbnbEmailWebOAuthStartResult>>,
            AuditStartAirbnbEmailWebOAuthBehavior>();
        services.AddScoped<
            IPipelineBehavior<StartAirbnbEmailWebOAuthCommand, Result<AirbnbEmailWebOAuthStartResult>>,
            TenantTransactionBehavior<StartAirbnbEmailWebOAuthCommand, Result<AirbnbEmailWebOAuthStartResult>, ExternalIntegrationsDbContext>>();

        return services;
    }
}
