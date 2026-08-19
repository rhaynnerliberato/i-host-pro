using IHostPro.BuildingBlocks.Application;
using IHostPro.Contexts.Communication.Application;
using IHostPro.Contexts.Communication.Infrastructure.Messaging;
using IHostPro.Contexts.Communication.Infrastructure.Persistence;
using IHostPro.Contexts.Reservations.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace IHostPro.Contexts.Communication.Infrastructure;

/// <summary>
/// Single composition-root entry point for the Communication module (Fase
/// 9, Checkpoint 1) — mirrors <c>DashboardModuleExtensions</c> exactly.
/// Consumed exclusively in <c>IHostPro.Worker</c> this checkpoint —
/// <c>IHostPro.Api</c> never references it (no HTTP surface, CP1 mandate
/// §44/§45).
/// </summary>
public static class CommunicationModuleExtensions
{
    public static IServiceCollection AddCommunicationModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDbContext<CommunicationDbContext>(options =>
            options.UseNpgsql(
                configuration.GetConnectionString("Communication"),
                npgsqlOptions => npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "communication")));

        services.AddSingleton(TimeProvider.System);

        return services;
    }

    /// <summary>
    /// Registers everything <see cref="ReservationCreatedCommunicationProcessor"/>'s
    /// own dependency graph needs, so <see cref="CommunicationMessageExecutionScope"/>
    /// (ADR-016) can construct it from its own child DI scope — mirrors
    /// <c>AddDashboardProjectionConsumer</c>'s own two-call split exactly.
    /// </summary>
    public static IServiceCollection AddCommunicationReservationConsumer(this IServiceCollection services)
    {
        services.AddScoped<IMessageRepository, MessageRepository>();
        services.AddScoped<ICommunicationTransactionExecutor, CommunicationTransactionExecutor>();

        // Fase 9, Checkpoint 1 (CP1 mandate §11/§13): the ONLY connector
        // implementation this checkpoint has — a deterministic fake, never a
        // real WhatsApp API client.
        services.AddScoped<IOutboundMessageConnector, FakeWhatsAppConnector>();

        services.AddKeyedScoped<IIntegrationEventHandler<ReservationCreated>, ReservationCreatedCommunicationProcessor>(
            CommunicationMessageExecutionScope.HandlerKey);

        services.AddScoped<ICommunicationMessageExecutionScope, CommunicationMessageExecutionScope>();

        return services;
    }
}
