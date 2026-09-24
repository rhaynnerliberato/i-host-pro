using IHostPro.Contexts.Identity.Application;

namespace IHostPro.Contexts.Identity.Infrastructure.Caching;

/// <inheritdoc cref="ITenantAccessStateCache"/>
/// <remarks>
/// The default registered by <c>AddIdentityModule</c> for both hosts —
/// always allows, mirroring <see cref="NullSessionRevocationCache"/>'s own
/// role for <c>ISessionRevocationCache</c>. <c>IHostPro.Worker</c> never
/// validates a JWT and must still resolve a valid
/// <see cref="ITenantAccessStateCache"/> without crashing at DI-validation
/// time. <c>IHostPro.Api</c> overrides this registration with
/// <c>RedisTenantAccessStateCache</c> via <c>AddIdentityTenantAccessStateCache</c>.
/// </remarks>
public sealed class NullTenantAccessStateCache : ITenantAccessStateCache
{
    public Task MarkSuspendedAsync(Guid tenantId, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task MarkReactivatedAsync(Guid tenantId, DateTimeOffset reactivatedAtUtc, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task<bool> IsAccessAllowedAsync(Guid tenantId, DateTimeOffset credentialIssuedAtUtc, CancellationToken cancellationToken) =>
        Task.FromResult(true);
}
