using FluentAssertions;
using IHostPro.BuildingBlocks.Infrastructure.RateLimiting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using Testcontainers.Redis;

namespace IHostPro.Contexts.Configuration.Tests.Integration;

/// <summary>
/// Fase 12, Checkpoint 3 (Resilience &amp; Rate Limiting) — proves
/// <see cref="RedisFixedWindowRateLimiter"/>'s core behaviors against a real
/// Redis (Testcontainers, own dedicated container — never the shared
/// standing dev one). Lives in this project (not a new dedicated test
/// project) as a deliberate, pragmatic choice for this checkpoint: it
/// already carries the exact Testcontainers.Redis + BuildingBlocks.Infrastructure
/// wiring this suite needs, and adding a brand-new test project would also
/// require updating the CI matrix's explicit project list — a larger,
/// separately-scoped change this checkpoint's mandate did not ask for.
/// <see cref="RedisFixedWindowRateLimiter"/> itself is fully host-agnostic
/// (BuildingBlocks.Infrastructure) — this class exercises it directly, never
/// through HTTP or Wolverine.
/// </summary>
public sealed class DistributedRateLimiterTests : IAsyncLifetime
{
    private RedisContainer _redisContainer = null!;

    public async Task InitializeAsync()
    {
        _redisContainer = new RedisBuilder().WithImage("redis:7-alpine").Build();
        await _redisContainer.StartAsync();
    }

    public async Task DisposeAsync() => await _redisContainer.DisposeAsync();

    private RedisFixedWindowRateLimiter BuildLimiter(Dictionary<string, RateLimitPolicyOptions> policies)
    {
        var connectionMultiplexer = ConnectionMultiplexer.Connect(_redisContainer.GetConnectionString());
        var options = Options.Create(new RateLimitingOptions { Enabled = true, Policies = policies });
        return new RedisFixedWindowRateLimiter(connectionMultiplexer, options, NullLogger<RedisFixedWindowRateLimiter>.Instance);
    }

    [Fact]
    public async Task Requests_below_the_limit_are_allowed()
    {
        var limiter = BuildLimiter(new()
        {
            ["Test"] = new RateLimitPolicyOptions { PermitLimit = 3, Window = TimeSpan.FromMinutes(1) },
        });

        for (var i = 0; i < 3; i++)
        {
            var decision = await limiter.CheckAsync("Test", "partition-a", CancellationToken.None);
            decision.Allowed.Should().BeTrue($"call {i + 1} of 3 is still within the limit");
        }
    }

    [Fact]
    public async Task A_request_exceeding_the_limit_is_denied_with_a_retry_after()
    {
        var limiter = BuildLimiter(new()
        {
            ["Test"] = new RateLimitPolicyOptions { PermitLimit = 2, Window = TimeSpan.FromMinutes(1) },
        });

        await limiter.CheckAsync("Test", "partition-a", CancellationToken.None);
        await limiter.CheckAsync("Test", "partition-a", CancellationToken.None);
        var thirdDecision = await limiter.CheckAsync("Test", "partition-a", CancellationToken.None);

        thirdDecision.Allowed.Should().BeFalse("the 3rd call exceeds a limit of 2");
        thirdDecision.RetryAfter.Should().NotBeNull();
        thirdDecision.RetryAfter!.Value.Should().BeGreaterThan(TimeSpan.Zero).And.BeLessThanOrEqualTo(TimeSpan.FromMinutes(1));
    }

    /// <summary>Fase 12, CP3, Decision Gate §5 — MultiTenantFairnessProven=true: Tenant A exceeding its own limit must never affect Tenant B.</summary>
    [Fact]
    public async Task One_partition_exceeding_its_limit_never_affects_a_different_partition()
    {
        var limiter = BuildLimiter(new()
        {
            ["TenantApi"] = new RateLimitPolicyOptions { PermitLimit = 2, Window = TimeSpan.FromMinutes(1) },
        });

        await limiter.CheckAsync("TenantApi", "tenant-a", CancellationToken.None);
        await limiter.CheckAsync("TenantApi", "tenant-a", CancellationToken.None);
        var tenantAThirdCall = await limiter.CheckAsync("TenantApi", "tenant-a", CancellationToken.None);
        tenantAThirdCall.Allowed.Should().BeFalse("Tenant A already used its full quota");

        var tenantBFirstCall = await limiter.CheckAsync("TenantApi", "tenant-b", CancellationToken.None);
        var tenantBSecondCall = await limiter.CheckAsync("TenantApi", "tenant-b", CancellationToken.None);

        tenantBFirstCall.Allowed.Should().BeTrue("Tenant B has its own independent counter");
        tenantBSecondCall.Allowed.Should().BeTrue("Tenant B has its own independent counter");
    }

    [Fact]
    public async Task Different_policies_never_share_a_counter_even_for_the_same_partition_key()
    {
        var limiter = BuildLimiter(new()
        {
            ["PolicyOne"] = new RateLimitPolicyOptions { PermitLimit = 1, Window = TimeSpan.FromMinutes(1) },
            ["PolicyTwo"] = new RateLimitPolicyOptions { PermitLimit = 1, Window = TimeSpan.FromMinutes(1) },
        });

        var firstPolicyFirstCall = await limiter.CheckAsync("PolicyOne", "same-key", CancellationToken.None);
        var secondPolicyFirstCall = await limiter.CheckAsync("PolicyTwo", "same-key", CancellationToken.None);

        firstPolicyFirstCall.Allowed.Should().BeTrue();
        secondPolicyFirstCall.Allowed.Should().BeTrue("a different policy name is a different counter, even for the identical partition key");
    }

    /// <summary>Fase 12, CP3, Decision Gate §2/§28 — Redis down: FailOpen never blocks, FailClosed always blocks, per policy.</summary>
    [Fact]
    public async Task Redis_outage_honors_each_policys_own_configured_failure_mode()
    {
        var failOpenLimiter = BuildLimiter(new()
        {
            ["Webhook"] = new RateLimitPolicyOptions { PermitLimit = 1, Window = TimeSpan.FromMinutes(1), FailureMode = RateLimitFailureMode.FailOpen },
        });
        var failClosedLimiter = BuildLimiter(new()
        {
            ["Authentication"] = new RateLimitPolicyOptions { PermitLimit = 1, Window = TimeSpan.FromMinutes(1), FailureMode = RateLimitFailureMode.FailClosed },
        });

        await _redisContainer.StopAsync();
        try
        {
            var failOpenDecision = await failOpenLimiter.CheckAsync("Webhook", "some-account", CancellationToken.None);
            var failClosedDecision = await failClosedLimiter.CheckAsync("Authentication", "some-ip", CancellationToken.None);

            failOpenDecision.Allowed.Should().BeTrue("FailOpen must never block a legitimate request just because Redis is down");
            failOpenDecision.FailedOpen.Should().BeTrue();
            failClosedDecision.Allowed.Should().BeFalse("FailClosed must protect authentication even when Redis is down");
        }
        finally
        {
            await _redisContainer.StartAsync();
        }
    }

    [Fact]
    public async Task An_unconfigured_policy_name_is_always_allowed()
    {
        var limiter = BuildLimiter(new());

        var decision = await limiter.CheckAsync("NeverConfigured", "anything", CancellationToken.None);

        decision.Allowed.Should().BeTrue("a policy no one configured is treated as unlimited, never as a silent denial");
    }
}
