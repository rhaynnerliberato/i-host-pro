using FluentAssertions;

namespace IHostPro.Web.Tests.E2E;

/// <summary>
/// Exercises <see cref="WebE2EFixture"/>'s own <c>DisposeAsync</c> directly —
/// deliberately never calling <c>InitializeAsync</c> first, so every internal
/// field (containers, processes, Playwright, browser) is still at its
/// never-created default. Proves two of the required lifecycle guarantees
/// cheaply and directly against the real type, without booting anything:
/// DisposeAsync must not fail when part (here, all) of the infrastructure
/// was never created, and calling it more than once must be safe. This
/// intentionally does not construct <see cref="WebE2EFixture"/> through
/// <see cref="WebE2EFixtureCollection"/> — it is a standalone, unmanaged
/// instance used only for this narrow check.
/// </summary>
public sealed class WebE2EFixtureCleanupTests
{
    [Fact]
    public async Task DisposeAsync_on_a_never_initialized_fixture_does_not_throw()
    {
        var fixture = new WebE2EFixture();

        var act = async () => await fixture.DisposeAsync();

        await act.Should().NotThrowAsync("none of the fixture's infrastructure was ever created, so there is nothing to clean up — DisposeAsync must recognize that, not assume InitializeAsync always ran first");
    }

    [Fact]
    public async Task DisposeAsync_on_a_never_initialized_fixture_is_idempotent()
    {
        var fixture = new WebE2EFixture();

        await fixture.DisposeAsync();
        var act = async () => await fixture.DisposeAsync();

        await act.Should().NotThrowAsync("a second DisposeAsync call must be a safe no-op, whether or not the fixture was ever initialized");
    }
}
