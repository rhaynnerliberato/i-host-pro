namespace IHostPro.Contexts.Identity.Infrastructure.Caching;

/// <summary>
/// Redis connection configuration for <see cref="RedisSessionRevocationCache"/>
/// (Incremento 2 plan, Etapa 12). Registered — and therefore bound/validated
/// — exclusively by <c>AddIdentitySessionRevocationCache</c>, called only
/// from <c>IHostPro.Api</c>'s composition root; <c>IHostPro.Worker</c> never
/// binds or validates this section at all.
///
/// <see cref="ConnectTimeout"/>/<see cref="OperationTimeout"/>/<see cref="ConnectRetry"/>
/// govern how long <see cref="RedisSessionRevocationCache"/>'s fail-open catch
/// (see its own doc comment) takes to trigger. Confirmed empirically
/// (Incremento 2 plan, homologação real) that StackExchange.Redis's own
/// defaults (ConnectTimeout/SyncTimeout/AsyncTimeout = 5000ms, ConnectRetry =
/// 3) made every Redis operation — including <c>IsRevokedAsync</c>, called on
/// every authenticated request via JWT Bearer validation, not just Logout —
/// block for around 11 seconds while Redis was unreachable before the
/// existing fail-open catch took effect. Fail-open behavior itself (Redis is
/// never the source of truth) is unchanged; only how quickly it engages.
/// </summary>
public sealed class SessionRevocationCacheOptions
{
    public const string SectionName = "Identity:SessionRevocationCache";

    /// <summary>StackExchange.Redis connection string (e.g. <c>"localhost:6379"</c>) — never a URI.</summary>
    public string ConnectionString { get; set; } = string.Empty;

    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Applied to both StackExchange.Redis's SyncTimeout and AsyncTimeout — every operation this cache performs is async.</summary>
    public TimeSpan OperationTimeout { get; set; } = TimeSpan.FromSeconds(1);

    public int ConnectRetry { get; set; } = 1;
}
