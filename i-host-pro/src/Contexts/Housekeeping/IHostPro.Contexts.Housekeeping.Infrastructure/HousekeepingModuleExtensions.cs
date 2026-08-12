using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.Housekeeping.Application;
using IHostPro.Contexts.Housekeeping.Application.Checklist;
using IHostPro.Contexts.Housekeeping.Application.Cleanings;
using IHostPro.Contexts.Housekeeping.Application.Occurrences;
using IHostPro.Contexts.Housekeeping.Domain;
using IHostPro.Contexts.Housekeeping.Infrastructure.Messaging;
using IHostPro.Contexts.Housekeeping.Infrastructure.Persistence;
using IHostPro.Contexts.Housekeeping.Infrastructure.Projections;
using IHostPro.Contexts.PropertyManagement.Contracts;
using IHostPro.Contexts.Reservations.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace IHostPro.Contexts.Housekeeping.Infrastructure;

/// <summary>
/// Single composition-root entry point for the parts of the Housekeeping
/// module BOTH <c>IHostPro.Api</c> and <c>IHostPro.Worker</c> need (Fase 6,
/// Incremento 1) — unlike Configuration &amp; Policy (whose Worker only
/// needs the cache, never <c>ConfigurationDbContext</c>), Housekeeping's
/// Worker DOES need <see cref="HousekeepingDbContext"/>: its own local
/// Property/Reservation projections and the automatic
/// <c>ReservationCancelled</c> → Cleaning-cancellation reaction are both
/// real writes to this context's own schema, consumed exclusively in
/// <c>IHostPro.Worker</c>. Mediator/validators/pipeline-behaviors (HTTP
/// command/query dispatch) remain Api-only —
/// <see cref="HousekeepingCommandDispatchExtensions"/>.
/// </summary>
public static class HousekeepingModuleExtensions
{
    public static IServiceCollection AddHousekeepingModule(
        this IServiceCollection services, IConfiguration configuration)
    {
        // The migrations history table must live inside the module's own
        // schema, never the default `public` — mirrors
        // ReservationsModuleExtensions/ConfigurationModuleExtensions.
        services.AddDbContext<HousekeepingDbContext>(options =>
            options.UseNpgsql(
                configuration.GetConnectionString("Housekeeping"),
                npgsqlOptions => npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "housekeeping")));

        services.AddSingleton(TimeProvider.System);

        services.AddScoped<IRepository<Cleaning, Guid>, CleaningRepository>();
        services.AddScoped<ICleaningReader, CleaningReader>();
        services.AddScoped<IHousekeepingAuditWriter, HousekeepingAuditWriter>();
        services.AddScoped<ICleaningOccurrenceWriter, CleaningOccurrenceWriter>();
        services.AddScoped<ICleaningOccurrenceReader, CleaningOccurrenceReader>();
        services.AddScoped<ICleaningChecklistItemRepository, CleaningChecklistItemRepository>();
        services.AddScoped<ICleaningChecklistReader, CleaningChecklistReader>();
        services.AddScoped<IPropertyReferenceProjection, PropertyReferenceProjectionReader>();
        services.AddScoped<IReservationReferenceProjection, ReservationReferenceProjectionReader>();

        // Backs every write command's transactional step, AND the two
        // Worker-side event reactions below — see
        // HousekeepingOutboxTransactionExecutor's own doc comment.
        services.AddScoped<IIntegrationEventCollector, IntegrationEventCollector>();
        services.AddScoped<IHousekeepingTransactionExecutor, HousekeepingOutboxTransactionExecutor>();

        // Typed wrapper adding DbUpdateConcurrencyException -> CleaningConcurrencyConflict
        // translation for every command that mutates an EXISTING Cleaning row
        // — see ICleaningTransitionExecutor's own doc comment.
        services.AddScoped<ICleaningTransitionExecutor, CleaningTransitionExecutor>();

        // Property/Reservation projection synchronization + the automatic
        // ReservationCancelled -> Cleaning cancellation reaction (Checkpoint
        // 3) — consumed exclusively in IHostPro.Worker via the six Wolverine
        // adapters in Housekeeping.Infrastructure.Messaging. Each interface
        // registered separately (rather than a single shared instance) is
        // safe: both synchronizer classes are stateless per call.
        services.AddScoped<IIntegrationEventHandler<PropertyCreated>, PropertyProjectionSynchronizer>();
        services.AddScoped<IIntegrationEventHandler<PropertyActivated>, PropertyProjectionSynchronizer>();
        services.AddScoped<IIntegrationEventHandler<PropertyDeactivated>, PropertyProjectionSynchronizer>();
        services.AddScoped<IIntegrationEventHandler<PropertyArchived>, PropertyProjectionSynchronizer>();
        services.AddScoped<IIntegrationEventHandler<ReservationCreated>, ReservationProjectionAndCancellationReaction>();
        services.AddScoped<IIntegrationEventHandler<ReservationCancelled>, ReservationProjectionAndCancellationReaction>();

        // ADR-015 (Fase 6, Checkpoint 6) — isolates Housekeeping's own
        // tenant/persistence/transaction ownership from Wolverine's
        // per-message DI resolution graph. The single class in this module
        // authorized to hold IServiceScopeFactory — see its own doc comment.
        services.AddScoped<IHousekeepingMessageExecutionScope, HousekeepingMessageExecutionScope>();

        return services;
    }
}
