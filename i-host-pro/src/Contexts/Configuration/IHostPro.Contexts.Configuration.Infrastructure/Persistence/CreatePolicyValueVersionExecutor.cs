using IHostPro.BuildingBlocks.Domain;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.BuildingBlocks.Infrastructure.Persistence;
using IHostPro.Contexts.Configuration.Application;
using IHostPro.Contexts.Configuration.Application.Errors;
using IHostPro.Contexts.Configuration.Application.Policies;
using IHostPro.Contexts.Configuration.Infrastructure.Caching;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wolverine.EntityFrameworkCore;
using Wolverine.Runtime;

namespace IHostPro.Contexts.Configuration.Infrastructure.Persistence;

/// <inheritdoc cref="ICreatePolicyValueVersionExecutor"/>
/// <remarks>
/// Opens its own write transaction via <see cref="TenantAwareTransactionScope"/>
/// (unchanged since Checkpoint 4) and, from Checkpoint 6 on, also publishes
/// through this context's transactional outbox — mirrors
/// <c>ReservationsOutboxTransactionExecutor</c> exactly, including
/// <see cref="MessageContext.OverrideStorage"/>: without it,
/// <see cref="IDbContextOutbox{TDbContext}"/> inherits
/// <c>Wolverine.Runtime.MessageBus</c>'s constructor, which unconditionally
/// targets the Main store (<c>platform_messaging</c>) instead of this
/// context's own Ancillary store (<c>configuration_messaging</c>) — see that
/// class's own doc comment for the full root-cause explanation (Fase 2,
/// Checkpoint 6 homologação). Configuration has exactly one write command in
/// this increment, so — unlike Reservations/PropertyManagement, which share
/// one generic transaction executor across several commands — the outbox
/// logic lives directly here rather than behind a second, otherwise-unused
/// abstraction layer.
///
/// Checkpoint 7 homologação, real defect found and fixed: also calls
/// <see cref="IPolicyCacheInvalidator.InvalidateAsync"/> directly, right
/// after the write commits — in addition to, never instead of, publishing
/// <c>PolicyUpdated</c> above (kept for any future consumer outside this
/// context). Confirmed by direct observation, driving the real UI end to
/// end: the write's own HTTP response can return before the async
/// outbox→RabbitMQ→<c>IHostPro.Worker</c> round-trip has necessarily
/// invalidated the cache, so a caller that re-reads the effective value
/// immediately after a successful write (the Policies admin screen's own
/// "reload the effective value after saving" behavior) can race the async
/// invalidation and read a still-stale cached result — and since nothing
/// re-fetches on its own afterward, that staleness never self-corrects.
/// Invalidating synchronously here, in the same request that performed the
/// write, removes the race at its source rather than asking every caller to
/// poll or retry.
///
/// This call is wrapped in its own try/catch, unlike everything else in
/// <see cref="ExecuteAsync"/> — a real regression found immediately after
/// first adding it, unguarded: <see cref="IPolicyCacheInvalidator.InvalidateAsync"/>'s
/// own contract deliberately never swallows a genuine cache failure (see
/// <c>RedisPolicyValueCache</c>'s doc comment) so Wolverine's own
/// retry/circuit-breaker can handle it when this same method runs inside
/// <c>PolicyUpdatedCacheInvalidation</c> — but called synchronously here,
/// that same unswallowed exception propagated out of an otherwise fully
/// successful HTTP write (the Postgres commit and outbox flush had already
/// both succeeded) and turned a transient Redis outage into a false <c>500</c>
/// for a write that had, in fact, already succeeded. The write's own success
/// was never contingent on this optimization landing; a failure here is
/// logged and left for the async <c>PolicyUpdated</c> path (which already
/// has its own retry semantics) to eventually correct instead.
/// </remarks>
public sealed class CreatePolicyValueVersionExecutor : ICreatePolicyValueVersionExecutor
{
    private static readonly Error VersionConflictError = new(PolicyErrorCodes.VersionConflict, PolicyErrorCodes.VersionConflict);

    private readonly ConfigurationDbContext _dbContext;
    private readonly ITenantContext _tenantContext;
    private readonly IIntegrationEventCollector _collector;
    private readonly IDbContextOutbox<ConfigurationDbContext> _outbox;
    private readonly IPolicyCacheInvalidator _cacheInvalidator;
    private readonly ILogger<CreatePolicyValueVersionExecutor> _logger;

    public CreatePolicyValueVersionExecutor(
        ConfigurationDbContext dbContext,
        ITenantContext tenantContext,
        IIntegrationEventCollector collector,
        IDbContextOutbox<ConfigurationDbContext> outbox,
        IWolverineRuntime runtime,
        IPolicyCacheInvalidator cacheInvalidator,
        ILogger<CreatePolicyValueVersionExecutor> logger)
    {
        _dbContext = dbContext;
        _tenantContext = tenantContext;
        _collector = collector;
        _outbox = outbox;
        _cacheInvalidator = cacheInvalidator;
        _logger = logger;

        if (_outbox is not MessageContext messageContext)
        {
            throw new InvalidOperationException(
                "The configured Wolverine ConfigurationDbContext outbox does not support explicit message store selection.");
        }

        var configurationStore = runtime.FindAncillaryStoreForMarkerType(typeof(ConfigurationDbContext));
        messageContext.OverrideStorage(configurationStore);
    }

    public async Task<Result<PolicyValueDetailResult>> ExecuteAsync(
        Func<Task<Result<PolicyValueDetailResult>>> operation, CancellationToken cancellationToken)
    {
        _collector.Drain();

        await using var transaction = await TenantAwareTransactionScope.BeginAsync(
            _dbContext, _tenantContext, readOnly: false, cancellationToken);

        try
        {
            var result = await operation();

            foreach (var @event in _collector.Drain())
                await _outbox.PublishAsync(@event);

            // No explicit transaction.CommitAsync() — SaveChangesAndFlushMessagesAsync
            // itself commits the ambient transaction TenantAwareTransactionScope opened
            // (mirrors ReservationsOutboxTransactionExecutor exactly), so the domain
            // change and the staged outbox row commit atomically in one call.
            await _outbox.SaveChangesAndFlushMessagesAsync(cancellationToken);

            if (result.IsSuccess)
            {
                try
                {
                    await _cacheInvalidator.InvalidateAsync(_tenantContext.TenantId!.Value, result.Value.PolicyCode, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(
                        ex,
                        "Failed to synchronously invalidate the policy cache for tenant {TenantId}, policy {PolicyCode} after a successful write — the write itself already succeeded; the async PolicyUpdated consumer will still correct the cache once it runs.",
                        _tenantContext.TenantId, result.Value.PolicyCode);
                }
            }

            return result;
        }
        catch (DbUpdateException)
        {
            // The partial unique index's own last-resort defense against a
            // genuine concurrent write racing past the handler's own
            // expected-version pre-check.
            _dbContext.ChangeTracker.Clear();
            _collector.Drain();

            return Result.Failure<PolicyValueDetailResult>(VersionConflictError);
        }
        catch
        {
            _dbContext.ChangeTracker.Clear();
            _collector.Drain();
            throw;
        }
    }
}
