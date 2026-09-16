using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.Contexts.ExternalIntegrations.Application.AirbnbEmailBridge;
using IHostPro.Contexts.ExternalIntegrations.Infrastructure.AirbnbEmailBridge;
using Microsoft.Extensions.Options;

namespace IHostPro.Worker.AirbnbEmailBridge;

/// <summary>
/// Periodic Airbnb Email Bridge delta-sync poller (Fase 9 review — Delta
/// Polling gate). Mirrors <c>DeadLetterMetricsBackgroundService</c>'s own
/// loop/cancellation/logging skeleton exactly.
///
/// Always registered (matches every other <c>BackgroundService</c> in this
/// Worker — none are Development-gated), but a no-op tick whenever
/// <see cref="AirbnbEmailBridgeOptions.PollingEnabled"/> is false or no
/// tenant is configured (Fase 9 review §46: never read a mailbox merely
/// because the process started). Each configured tenant gets its own DI
/// scope per tick — <c>ITenantContext</c>/the DbContext are Scoped, and this
/// service itself is a Singleton hosted service.
/// </summary>
public sealed class AirbnbEmailDeltaPollingBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptions<AirbnbEmailBridgeOptions> _options;
    private readonly ILogger<AirbnbEmailDeltaPollingBackgroundService> _logger;

    public AirbnbEmailDeltaPollingBackgroundService(
        IServiceScopeFactory scopeFactory, IOptions<AirbnbEmailBridgeOptions> options, ILogger<AirbnbEmailDeltaPollingBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await PollOnceAsync(stoppingToken);

            var options = _options.Value;
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(options.PollingIntervalSeconds, 1)), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task PollOnceAsync(CancellationToken stoppingToken)
    {
        var options = _options.Value;
        if (!options.PollingEnabled || options.PollingTenantIds.Length == 0)
            return;

        foreach (var tenantId in options.PollingTenantIds)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(tenantId);
                var runner = scope.ServiceProvider.GetRequiredService<IAirbnbEmailDeltaSyncRunner>();

                await runner.RunAsync(tenantId, options.MailFolderId, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Airbnb Email Bridge delta sync tick failed for tenant {TenantId} — will retry next cycle.", tenantId);
            }
        }
    }
}
