using IHostPro.Contexts.Identity.Application;

namespace IHostPro.Contexts.Identity.Infrastructure.Caching;

/// <inheritdoc cref="ISessionRevocationCache"/>
/// <remarks>
/// The default registered by <c>AddIdentityModule</c> for both hosts — a
/// silent no-op. <c>IHostPro.Worker</c> never gets a real Redis-backed
/// implementation (Incremento 2 plan, Etapa 12: "registro somente no host
/// API") and must still resolve a valid <see cref="ISessionRevocationCache"/>
/// without crashing at DI-validation time; this also keeps this cache's own
/// documented contract exactly true when no real cache is configured at all
/// — "Redis is never the source of truth", so its complete absence is simply
/// another case that degrades to "nothing cached, PostgreSQL decides".
/// <c>IHostPro.Api</c> overrides this registration with
/// <c>RedisSessionRevocationCache</c> via <c>AddIdentitySessionRevocationCache</c>.
/// </remarks>
public sealed class NullSessionRevocationCache : ISessionRevocationCache
{
    public Task MarkRevokedAsync(Guid tenantId, Guid sessionId, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task<bool> IsRevokedAsync(Guid tenantId, Guid sessionId, CancellationToken cancellationToken) =>
        Task.FromResult(false);
}
