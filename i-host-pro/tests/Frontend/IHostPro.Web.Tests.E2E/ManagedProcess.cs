using System.Diagnostics;

namespace IHostPro.Web.Tests.E2E;

/// <summary>
/// Wraps a single child process a test fixture itself started, guaranteeing
/// idempotent, verified teardown. Registered the instant <see cref="Process.Start(ProcessStartInfo)"/>
/// returns, so it is always a valid disposal candidate even if a later step
/// in the caller's own startup sequence throws before that sequence
/// otherwise would have recorded it anywhere.
///
/// Extracted out of <see cref="WebE2EFixture"/> so its teardown semantics —
/// bounded-wait kill, a Windows taskkill fallback scoped to the exact PID
/// this instance started (never a name-based kill), and idempotency — can be
/// exercised directly against real (but lightweight) child processes without
/// booting the full Postgres/RabbitMQ/Redis/API/Angular stack. See
/// <c>ManagedProcessTests</c>.
/// </summary>
internal sealed class ManagedProcess
{
    private readonly Process _process;
    private readonly string _name;
    private int _stopped;

    private ManagedProcess(Process process, string name)
    {
        _process = process;
        _name = name;
    }

    public int Id => _process.Id;

    /// <summary>Starts the process and immediately begins draining stdout/stderr asynchronously (event-based <see cref="Process.BeginOutputReadLine"/>/<see cref="Process.BeginErrorReadLine"/>) so a full output buffer can never deadlock the child — the same mechanism already relied on before this refactor, unchanged.</summary>
    public static ManagedProcess Start(ProcessStartInfo startInfo, string name)
    {
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.UseShellExecute = false;

        var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Failed to start '{name}'.");
        var managed = new ManagedProcess(process, name);
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, _) => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return managed;
    }

    /// <summary>
    /// Idempotent — a second (or later) call is a no-op that returns null
    /// immediately. Never throws: any failure is returned as a diagnostic
    /// string instead of propagating, so a teardown problem can never mask
    /// the caller's own original failure (the caller decides what to do with
    /// a non-null result — log it, aggregate it, or surface it as its own
    /// failure once every other teardown step has also had a chance to run).
    /// </summary>
    public async Task<string?> StopAsync(TimeSpan timeout)
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0)
            return null;

        try
        {
            if (_process.HasExited)
                return null;

            try
            {
                _process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                return null; // Exited between the HasExited check above and Kill().
            }

            using var cts = new CancellationTokenSource(timeout);
            try
            {
                await _process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                // Kill(entireProcessTree: true) alone did not reap it within the timeout — on
                // Windows a grandchild can detach from the tracked process tree (e.g. its own
                // console/job-object handling) and survive a tree-kill. Fall back to a taskkill
                // scoped to this exact PID — never a name-based kill, which could hit an
                // unrelated process sharing the same executable name.
                await RunTaskkillFallbackAsync();
            }

            return _process.HasExited
                ? null
                : $"'{_name}' (pid {_process.Id}) did not exit within {timeout} even after the taskkill fallback.";
        }
        catch (Exception ex)
        {
            return $"Unexpected error stopping '{_name}' (pid {_process.Id}): {ex.Message}";
        }
    }

    private async Task RunTaskkillFallbackAsync()
    {
        if (!OperatingSystem.IsWindows())
            return;

        try
        {
            var psi = new ProcessStartInfo("taskkill", $"/PID {_process.Id} /T /F")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var taskkill = Process.Start(psi);
            if (taskkill is not null)
                await taskkill.WaitForExitAsync();
        }
        catch
        {
            // Best-effort only — the caller re-checks _process.HasExited afterward regardless,
            // so a failure here surfaces as the normal "did not exit" diagnostic, never silently.
        }
    }
}
