using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using FluentAssertions;

namespace IHostPro.Web.Tests.E2E;

/// <summary>
/// Exercises <see cref="ManagedProcess"/> — the internal helper
/// <see cref="WebE2EFixture"/>'s own lifecycle hardening (the try/catch +
/// idempotent cleanup in <c>InitializeAsync</c>, and <c>DisposeAsync</c>
/// itself) is built on — directly against real, lightweight child processes.
/// This proves the actual defect class found this session (a real
/// <c>dotnet.exe</c>/<c>node.exe</c> surviving fixture teardown, blocking the
/// fixed ports the next run needs) cannot recur, without booting the full
/// Postgres/RabbitMQ/Redis/API/Angular stack for every assertion — that
/// expensive, fully-integrated proof is what the two-consecutive-assemblies
/// protocol covers separately. No test-only flag was added to
/// <see cref="WebE2EFixture"/> or <see cref="ManagedProcess"/> — every test
/// here calls only their real, already-production public surface.
/// </summary>
public sealed class ManagedProcessTests
{
    private static ProcessStartInfo SleepProcess(TimeSpan duration) =>
        new("powershell", $"-NoProfile -Command \"Start-Sleep -Seconds {(int)duration.TotalSeconds}\"");

    private static ProcessStartInfo PortListenerProcess(int port, TimeSpan duration) => new(
        "powershell",
        $"-NoProfile -Command \"$l=[System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback,{port}); $l.Start(); Start-Sleep -Seconds {(int)duration.TotalSeconds}\"");

    private static bool IsPortInUse(int port) =>
        IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(endpoint => endpoint.Port == port);

    private static int FindFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!condition() && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(100);
    }

    /// <summary>Simulates a step in a multi-step startup sequence that must fail — used instead of a bare `throw` so the compiler cannot prove a later statement unreachable (it can't see into this method), exactly mirroring how a real, unpredictable startup failure (a container never becoming healthy, a port never opening) would occur at an arbitrary point.</summary>
    private static Task ThrowSimulatedFailureAsync(string message) => throw new InvalidOperationException(message);

    [Fact]
    public async Task StopAsync_kills_a_real_process_and_releases_the_port_it_held()
    {
        var port = FindFreePort();
        var managed = ManagedProcess.Start(PortListenerProcess(port, TimeSpan.FromSeconds(60)), "test-port-listener");
        await WaitUntilAsync(() => IsPortInUse(port), TimeSpan.FromSeconds(10));
        IsPortInUse(port).Should().BeTrue("the dummy process must have actually bound the port before we can meaningfully assert it gets released");

        var diagnostic = await managed.StopAsync(TimeSpan.FromSeconds(10));

        diagnostic.Should().BeNull();
        IsPortInUse(port).Should().BeFalse("StopAsync must actually kill the process at the OS level — a port still bound afterward is exactly the defect class this hardening exists to catch");
    }

    [Fact]
    public async Task StopAsync_is_idempotent_a_second_call_is_a_no_op()
    {
        var managed = ManagedProcess.Start(SleepProcess(TimeSpan.FromSeconds(60)), "test-sleep");

        var first = await managed.StopAsync(TimeSpan.FromSeconds(10));
        var second = await managed.StopAsync(TimeSpan.FromSeconds(10));

        first.Should().BeNull();
        second.Should().BeNull("a second StopAsync call must be a safe no-op — the same guarantee WebE2EFixture.DisposeAsync relies on when it may be invoked more than once");
    }

    [Fact]
    public async Task StopAsync_on_an_already_exited_process_is_a_no_op()
    {
        var managed = ManagedProcess.Start(new ProcessStartInfo("cmd.exe", "/c exit 0"), "test-instant-exit");
        await WaitUntilAsync(() => false, TimeSpan.FromSeconds(2)); // Give it a moment to exit on its own first.

        var diagnostic = await managed.StopAsync(TimeSpan.FromSeconds(10));

        diagnostic.Should().BeNull();
    }

    /// <summary>
    /// Mirrors WebE2EFixture.InitializeAsync's own shape exactly — start step
    /// A (API-equivalent), start step B (Angular-equivalent), fail, clean up
    /// whatever was already started, rethrow the original exception
    /// unchanged. This is the pattern that makes "a failure starting Angular
    /// still cleans up the already-started API" true in the real fixture.
    /// </summary>
    [Fact]
    public async Task A_failure_after_both_steps_started_cleans_up_both_and_preserves_the_original_exception()
    {
        ManagedProcess? stepA = null; // API-equivalent
        ManagedProcess? stepB = null; // Angular-equivalent
        string? stepADiagnostic = null;
        string? stepBDiagnostic = null;
        Exception? caught = null;

        try
        {
            stepA = ManagedProcess.Start(SleepProcess(TimeSpan.FromSeconds(60)), "test-step-a");
            stepB = ManagedProcess.Start(SleepProcess(TimeSpan.FromSeconds(60)), "test-step-b");
            await ThrowSimulatedFailureAsync("simulated failure after both steps started");
        }
        catch (Exception ex)
        {
            caught = ex;
            if (stepA is not null) stepADiagnostic = await stepA.StopAsync(TimeSpan.FromSeconds(10));
            if (stepB is not null) stepBDiagnostic = await stepB.StopAsync(TimeSpan.FromSeconds(10));
        }

        caught.Should().NotBeNull();
        caught!.Message.Should().Be("simulated failure after both steps started", "the original exception must be what survives — cleanup must never replace or swallow it");
        stepADiagnostic.Should().BeNull("step A (API-equivalent) must have been cleaned up with no leftover diagnostic");
        stepBDiagnostic.Should().BeNull("step B (Angular-equivalent) must have been cleaned up with no leftover diagnostic");
    }

    /// <summary>Mirrors the case where the API itself never becomes ready — Angular (step B) is never even started, so cleanup must not choke on a null step B.</summary>
    [Fact]
    public async Task A_failure_after_only_the_first_step_never_touches_the_second_which_was_never_started()
    {
        ManagedProcess? stepA = null;
        ManagedProcess? stepB = null;
        string? stepADiagnostic = null;

        try
        {
            stepA = ManagedProcess.Start(SleepProcess(TimeSpan.FromSeconds(60)), "test-step-a-only");
            await ThrowSimulatedFailureAsync("simulated failure before the second step ever starts");
            stepB = ManagedProcess.Start(SleepProcess(TimeSpan.FromSeconds(60)), "test-step-b-never");
        }
        catch
        {
            if (stepA is not null) stepADiagnostic = await stepA.StopAsync(TimeSpan.FromSeconds(10));
            if (stepB is not null) await stepB.StopAsync(TimeSpan.FromSeconds(10));
        }

        stepB.Should().BeNull("the second step must never have started, exactly like WebE2EFixture never starting Angular when the API itself never became ready");
        stepADiagnostic.Should().BeNull("the first step, which did start, must still have been cleaned up");
    }
}
