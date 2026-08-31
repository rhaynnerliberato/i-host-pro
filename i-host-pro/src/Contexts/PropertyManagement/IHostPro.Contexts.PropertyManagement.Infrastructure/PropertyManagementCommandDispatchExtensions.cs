using FluentValidation;
using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.BuildingBlocks.Infrastructure.Persistence;
using IHostPro.Contexts.PropertyManagement.Application;
using IHostPro.Contexts.PropertyManagement.Application.Condominiums;
using IHostPro.Contexts.PropertyManagement.Application.FrontDesk;
using IHostPro.Contexts.PropertyManagement.Application.GuestAccess;
using IHostPro.Contexts.PropertyManagement.Application.Owners;
using IHostPro.Contexts.PropertyManagement.Application.Properties;
using IHostPro.Contexts.PropertyManagement.Domain;
using IHostPro.Contexts.PropertyManagement.Infrastructure.Persistence;
using Mediator;
using Microsoft.Extensions.DependencyInjection;

namespace IHostPro.Contexts.PropertyManagement.Infrastructure;

/// <summary>
/// Single composition-root entry point for dispatching Property Management's
/// write Commands (plus most read Queries, not yet needed anywhere but Api)
/// (Checkpoint 2 plan, item 12) — mirrors <c>IdentityCommandDispatchExtensions</c>
/// exactly. Called ONLY from <c>IHostPro.Api</c>'s composition root: dispatching
/// a write Command remains an HTTP-request concern.
///
/// Fase 11, Checkpoint 3 update: <see cref="GetPropertyDetailQuery"/>/
/// <see cref="GetPropertyAccessConfigurationQuery"/>'s own Application
/// Mediator wiring + <c>TenantTransactionBehavior&lt;,&gt;</c> moved to
/// <see cref="PropertyManagementModuleExtensions.AddPropertyManagementModule"/>
/// (called by both Api and Worker) so the AI Agent's own Worker-hosted Read
/// Tools can execute them in-process via <see cref="IPropertyManagementRequestDispatcher"/>
/// (Exception #3) — this method no longer calls
/// <c>AddPropertyManagementApplicationMediator</c> itself (Module already
/// did, earlier in the same container) and no longer re-registers those two
/// queries' behaviors. Every other Command/Query here stays exactly as it
/// was — write Commands/validators/write pipeline behaviors remain Api-only.
///
/// <see cref="CreateCondominiumCommand"/>/<see cref="UpdateCondominiumCommand"/>
/// each get their own CLOSED-generic tenant-aware behavior
/// (<see cref="CreateCondominiumTenantAwareBehavior"/>/
/// <see cref="UpdateCondominiumTenantAwareBehavior"/>), never the generic
/// <c>TenantTransactionBehavior&lt;,&gt;</c>, since both publish an
/// Integration Event that must reach the durable outbox atomically with the
/// domain change. <see cref="ListCondominiumsQuery"/>/
/// <see cref="GetCondominiumDetailQuery"/> are plain reads — no event, no
/// outbox — so each is registered with the shared, generic
/// <c>TenantTransactionBehavior&lt;,&gt;</c> directly, still closed per
/// message type (never an open generic — Architecture Principles §6).
///
/// Checkpoint 3 extends this with Property's own Create/Update/List/Detail
/// registrations, following the exact same shape:
/// <see cref="CreatePropertyCommand"/>/<see cref="UpdatePropertyCommand"/>
/// each get their own CLOSED-generic tenant-aware behavior; <see cref="ListPropertiesQuery"/>/
/// <see cref="GetPropertyDetailQuery"/> use the shared generic behavior.
///
/// Checkpoint 4 extends this further with the three lifecycle commands
/// (<c>ActivatePropertyCommand</c>/<c>DeactivatePropertyCommand</c>/
/// <c>ArchivePropertyCommand</c>) — no validators (no body, nothing
/// structural beyond the route-bound id), and all three share
/// <see cref="LifecyclePropertyTenantAwareBehavior{TCommand}"/>, still
/// registered as three separate CLOSED generics.
///
/// Checkpoint 5 (Ownership) extends this further. <c>LinkPropertyOwnerCommand</c>
/// deliberately gets NO wrapping pipeline behavior at all — its own handler
/// injects <see cref="ILinkPropertyOwnerExecutor"/> directly and opens
/// Property Management's write transaction itself, only after the
/// synchronous Identity eligibility check has already completed (see the
/// executor's own doc comment for why a wrapping behavior cannot express
/// this). <c>UnlinkPropertyOwnerCommand</c> follows the ordinary
/// wrapping-behavior shape, since it never calls Identity.
/// <c>ListPropertyOwnersQuery</c>/<c>ListMyPropertiesQuery</c>/
/// <c>GetMyPropertyDetailQuery</c> are plain reads, using the shared generic
/// behavior like every other query in this Bounded Context.
/// </summary>
public static class PropertyManagementCommandDispatchExtensions
{
    public static IServiceCollection AddPropertyManagementCommandDispatch(this IServiceCollection services)
    {
        // AddPropertyManagementApplicationMediator() is no longer called
        // here — AddPropertyManagementModule (called earlier, in the same
        // Api container) already registers it (Fase 11, Checkpoint 3 — see
        // this class's own doc comment).

        services.AddScoped<IValidator<CreateCondominiumCommand>, CreateCondominiumCommandValidator>();
        services.AddScoped<IValidator<UpdateCondominiumCommand>, UpdateCondominiumCommandValidator>();
        services.AddScoped<IValidator<ListCondominiumsQuery>, ListCondominiumsQueryValidator>();
        services.AddScoped<IValidator<SetFrontDeskContactCommand>, SetFrontDeskContactCommandValidator>();

        services.AddScoped<IValidator<CreatePropertyCommand>, CreatePropertyCommandValidator>();
        services.AddScoped<IValidator<UpdatePropertyCommand>, UpdatePropertyCommandValidator>();
        services.AddScoped<IValidator<ListPropertiesQuery>, ListPropertiesQueryValidator>();

        services.AddScoped<IValidator<LinkPropertyOwnerCommand>, LinkPropertyOwnerCommandValidator>();
        services.AddScoped<IValidator<ListPropertyOwnersQuery>, ListPropertyOwnersQueryValidator>();
        services.AddScoped<IValidator<ListMyPropertiesQuery>, ListMyPropertiesQueryValidator>();

        // Validation runs first for every command — safe as a single open
        // generic, it has no tenant/transaction side effects to collide
        // with. GetCondominiumDetailQuery/GetPropertyDetailQuery have no
        // validator registered — nothing meaningful to validate on a query
        // built exclusively from an already-model-bound route guid.
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));

        services.AddScoped<IRepository<Condominium, Guid>, CondominiumRepository>();
        services.AddScoped<ICondominiumReader, CondominiumReader>();
        services.AddScoped<IFrontDeskContactRepository, FrontDeskContactRepository>();
        // IPropertyAccessConfigurationRepository/IPropertyReader are
        // registered by AddPropertyManagementModule (Fase 11, Checkpoint 3)
        // — no longer duplicated here.
        services.AddScoped<IRepository<Property, Guid>, PropertyRepository>();
        services.AddScoped<IPropertyAuditWriter, PropertyAuditWriter>();
        services.AddScoped<IPropertyOwnerReader, PropertyOwnerReader>();
        services.AddScoped<IPropertyOwnerWriter, PropertyOwnerWriter>();

        // Backs every write command's transactional step (Checkpoint 2 plan,
        // item 12) — see PropertyManagementOutboxTransactionExecutor's own
        // doc comment.
        services.AddScoped<IIntegrationEventCollector, IntegrationEventCollector>();
        services.AddScoped<IPropertyManagementTransactionExecutor, PropertyManagementOutboxTransactionExecutor>();

        services.AddScoped<IUpdateCondominiumExecutor, UpdateCondominiumExecutor>();
        services.AddScoped<ICreatePropertyExecutor, CreatePropertyExecutor>();
        services.AddScoped<IUpdatePropertyExecutor, UpdatePropertyExecutor>();
        services.AddScoped<ILifecyclePropertyExecutor, LifecyclePropertyExecutor>();
        services.AddScoped<ILinkPropertyOwnerExecutor, LinkPropertyOwnerExecutor>();
        services.AddScoped<IUnlinkPropertyOwnerExecutor, UnlinkPropertyOwnerExecutor>();

        services.AddScoped<
            IPipelineBehavior<CreateCondominiumCommand, Result<CondominiumResult>>,
            CreateCondominiumTenantAwareBehavior>();
        services.AddScoped<
            IPipelineBehavior<UpdateCondominiumCommand, Result<CondominiumResult>>,
            UpdateCondominiumTenantAwareBehavior>();

        // Closed to PropertyManagementDbContext explicitly (Fase 4
        // homologation fix) — never the ambiguous, unparameterized DbContext
        // base type.
        services.AddScoped<
            IPipelineBehavior<ListCondominiumsQuery, Result<PagedResult<CondominiumSummaryResult>>>,
            TenantTransactionBehavior<ListCondominiumsQuery, Result<PagedResult<CondominiumSummaryResult>>, PropertyManagementDbContext>>();
        services.AddScoped<
            IPipelineBehavior<GetCondominiumDetailQuery, Result<CondominiumResult>>,
            TenantTransactionBehavior<GetCondominiumDetailQuery, Result<CondominiumResult>, PropertyManagementDbContext>>();

        // Fase 10, Checkpoint 4 (Portaria Notification Foundation):
        // SetFrontDeskContactCommand publishes no Integration Event, but
        // still gets its own closed-generic tenant-aware behavior (never the
        // shared generic TenantTransactionBehavior<,>) for consistency with
        // every other write command in this dispatch extension.
        // GetFrontDeskContactByCondominiumQuery is a plain read, using the
        // shared generic behavior like every other query here.
        services.AddScoped<
            IPipelineBehavior<SetFrontDeskContactCommand, Result<FrontDeskContactResult>>,
            SetFrontDeskContactTenantAwareBehavior>();
        services.AddScoped<
            IPipelineBehavior<GetFrontDeskContactByCondominiumQuery, Result<FrontDeskContactResult>>,
            TenantTransactionBehavior<GetFrontDeskContactByCondominiumQuery, Result<FrontDeskContactResult>, PropertyManagementDbContext>>();

        // Fase 10, Checkpoint 6.2 (Guest Access Secure Delivery Corrective
        // Implementation): SetPropertyAccessConfigurationCommand mirrors
        // SetFrontDeskContactCommand exactly — no Integration Event, its own
        // closed-generic tenant-aware behavior. GetPropertyAccessConfigurationQuery's
        // own behavior is registered by AddPropertyManagementModule (Fase 11,
        // Checkpoint 3) — not duplicated here.
        services.AddScoped<
            IPipelineBehavior<SetPropertyAccessConfigurationCommand, Result<PropertyAccessConfigurationResult>>,
            SetPropertyAccessConfigurationTenantAwareBehavior>();

        services.AddScoped<
            IPipelineBehavior<CreatePropertyCommand, Result<PropertyResult>>,
            CreatePropertyTenantAwareBehavior>();
        services.AddScoped<
            IPipelineBehavior<UpdatePropertyCommand, Result<PropertyResult>>,
            UpdatePropertyTenantAwareBehavior>();

        // GetPropertyDetailQuery's own behavior is registered by
        // AddPropertyManagementModule (Fase 11, Checkpoint 3) — not
        // duplicated here.
        services.AddScoped<
            IPipelineBehavior<ListPropertiesQuery, Result<PagedResult<PropertySummaryResult>>>,
            TenantTransactionBehavior<ListPropertiesQuery, Result<PagedResult<PropertySummaryResult>>, PropertyManagementDbContext>>();

        services.AddScoped<
            IPipelineBehavior<ActivatePropertyCommand, Result<PropertyResult>>,
            LifecyclePropertyTenantAwareBehavior<ActivatePropertyCommand>>();
        services.AddScoped<
            IPipelineBehavior<DeactivatePropertyCommand, Result<PropertyResult>>,
            LifecyclePropertyTenantAwareBehavior<DeactivatePropertyCommand>>();
        services.AddScoped<
            IPipelineBehavior<ArchivePropertyCommand, Result<PropertyResult>>,
            LifecyclePropertyTenantAwareBehavior<ArchivePropertyCommand>>();

        // LinkPropertyOwnerCommand: deliberately no pipeline behavior — see
        // this class's own doc comment and ILinkPropertyOwnerExecutor's.
        services.AddScoped<
            IPipelineBehavior<UnlinkPropertyOwnerCommand, Result>,
            UnlinkPropertyOwnerTenantAwareBehavior>();

        services.AddScoped<
            IPipelineBehavior<ListPropertyOwnersQuery, Result<PagedResult<PropertyOwnerResult>>>,
            TenantTransactionBehavior<ListPropertyOwnersQuery, Result<PagedResult<PropertyOwnerResult>>, PropertyManagementDbContext>>();
        services.AddScoped<
            IPipelineBehavior<ListMyPropertiesQuery, Result<PagedResult<PropertySummaryResult>>>,
            TenantTransactionBehavior<ListMyPropertiesQuery, Result<PagedResult<PropertySummaryResult>>, PropertyManagementDbContext>>();
        services.AddScoped<
            IPipelineBehavior<GetMyPropertyDetailQuery, Result<PropertyResult>>,
            TenantTransactionBehavior<GetMyPropertyDetailQuery, Result<PropertyResult>, PropertyManagementDbContext>>();

        return services;
    }
}
