using FluentValidation;
using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.BuildingBlocks.Infrastructure.Persistence;
using IHostPro.Contexts.Configuration.Application;
using IHostPro.Contexts.Configuration.Application.Policies;
using IHostPro.Contexts.Configuration.Application.Templates;
using IHostPro.Contexts.Configuration.Infrastructure.Persistence;
using IHostPro.Contexts.Configuration.Infrastructure.Resolution;
using Mediator;
using Microsoft.Extensions.DependencyInjection;

namespace IHostPro.Contexts.Configuration.Infrastructure;

/// <summary>
/// Single composition-root entry point for dispatching Configuration &amp;
/// Policy's Commands/Queries — mirrors <c>ReservationsCommandDispatchExtensions</c>
/// exactly. Called ONLY from <c>IHostPro.Api</c>'s composition root.
///
/// <see cref="ListPolicyDefinitionsQuery"/>/<see cref="GetPolicyValueByScopeQuery"/>/
/// <see cref="GetPolicyHistoryQuery"/> touch <c>ConfigurationDbContext</c>
/// directly and get the shared, generic
/// <c>TenantTransactionBehavior&lt;,,&gt;</c>, closed to
/// <see cref="ConfigurationDbContext"/> explicitly (Fase 4 homologação fix —
/// never the ambiguous, unparameterized <c>DbContext</c> base type).
///
/// <see cref="GetEffectivePolicyQuery"/> deliberately gets NO wrapping
/// behavior — it delegates entirely to the Checkpoint 3 typed readers
/// (<see cref="IHostPro.Contexts.Configuration.Contracts.IEarlyCheckInPolicyReader"/>/
/// <see cref="IHostPro.Contexts.Configuration.Contracts.ILateCheckoutPolicyReader"/>),
/// which open their own short-lived transaction on the same
/// <see cref="ConfigurationDbContext"/> instance — wrapping it here too would
/// nest a second transaction and throw <c>NestedUnitOfWorkException</c>.
///
/// <see cref="CreatePolicyValueVersionCommand"/> also gets no wrapping
/// behavior — mirrors <c>CreateReservationCommand</c>'s own precedent: its
/// handler injects <see cref="ICreatePolicyValueVersionExecutor"/> directly
/// and opens the write transaction itself.
/// </summary>
public static class ConfigurationCommandDispatchExtensions
{
    public static IServiceCollection AddConfigurationCommandDispatch(this IServiceCollection services)
    {
        services.AddConfigurationApplicationMediator();

        services.AddScoped<IValidator<CreatePolicyValueVersionCommand>, CreatePolicyValueVersionCommandValidator>();

        // Validation runs first for every command — safe as a single open
        // generic, no tenant/transaction side effects to collide with.
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));

        services.AddScoped<IPolicyDefinitionReader, PolicyDefinitionReader>();
        services.AddScoped<IPolicyValueReader, PolicyValueReader>();
        services.AddScoped<IPolicyValueRepository, PolicyValueRepository>();
        services.AddScoped<IPolicyAuditWriter, PolicyAuditWriter>();

        // Backs CreatePolicyValueVersionCommand's transactional/outbox step
        // (Checkpoint 6) — see CreatePolicyValueVersionExecutor's own doc comment.
        services.AddScoped<IIntegrationEventCollector, IntegrationEventCollector>();
        services.AddScoped<ICreatePolicyValueVersionExecutor, CreatePolicyValueVersionExecutor>();

        // Checkpoint 3's typed readers — registered here too (in addition to
        // AddConfigurationModule) is unnecessary; already registered there.
        // Nothing further needed for GetEffectivePolicyQueryHandler beyond
        // those existing registrations.

        services.AddScoped<
            IPipelineBehavior<ListPolicyDefinitionsQuery, Result<IReadOnlyList<PolicyDefinitionResult>>>,
            TenantTransactionBehavior<ListPolicyDefinitionsQuery, Result<IReadOnlyList<PolicyDefinitionResult>>, ConfigurationDbContext>>();
        services.AddScoped<
            IPipelineBehavior<GetPolicyValueByScopeQuery, Result<PolicyValueDetailResult>>,
            TenantTransactionBehavior<GetPolicyValueByScopeQuery, Result<PolicyValueDetailResult>, ConfigurationDbContext>>();
        services.AddScoped<
            IPipelineBehavior<GetPolicyHistoryQuery, Result<IReadOnlyList<PolicyValueDetailResult>>>,
            TenantTransactionBehavior<GetPolicyHistoryQuery, Result<IReadOnlyList<PolicyValueDetailResult>>, ConfigurationDbContext>>();

        // Fase 9, Checkpoint 1 — Template CRUD (administrative API only —
        // Communication's own read path uses ITemplateReader directly,
        // registered in AddConfigurationModule, never this Mediator
        // pipeline). No publish/outbox step needed (Templates publish no
        // Integration Event this checkpoint), so each command/query gets
        // the same plain TenantTransactionBehavior wrapping used by the
        // three read-only Policy queries above — no custom executor
        // required.
        services.AddScoped<ITemplateRepository, TemplateRepository>();
        services.AddScoped<IValidator<CreateTemplateCommand>, CreateTemplateCommandValidator>();
        services.AddScoped<IValidator<UpdateTemplateContentCommand>, UpdateTemplateContentCommandValidator>();

        services.AddScoped<
            IPipelineBehavior<CreateTemplateCommand, Result<TemplateResult>>,
            TenantTransactionBehavior<CreateTemplateCommand, Result<TemplateResult>, ConfigurationDbContext>>();
        services.AddScoped<
            IPipelineBehavior<UpdateTemplateContentCommand, Result<TemplateResult>>,
            TenantTransactionBehavior<UpdateTemplateContentCommand, Result<TemplateResult>, ConfigurationDbContext>>();
        services.AddScoped<
            IPipelineBehavior<SetTemplateActiveCommand, Result<TemplateResult>>,
            TenantTransactionBehavior<SetTemplateActiveCommand, Result<TemplateResult>, ConfigurationDbContext>>();
        services.AddScoped<
            IPipelineBehavior<GetTemplateByKeyQuery, Result<TemplateResult>>,
            TenantTransactionBehavior<GetTemplateByKeyQuery, Result<TemplateResult>, ConfigurationDbContext>>();

        return services;
    }
}
