namespace IHostPro.Contexts.Identity.Tests.Unit.Infrastructure;

/// <summary>Minimal deterministic <see cref="TimeProvider"/> test double — always returns a fixed instant.</summary>
internal sealed class FixedTimeProvider : TimeProvider
{
    private readonly DateTimeOffset _now;

    public FixedTimeProvider(DateTimeOffset now) => _now = now;

    public override DateTimeOffset GetUtcNow() => _now;
}
