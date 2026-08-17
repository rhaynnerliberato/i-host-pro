using Microsoft.Extensions.Logging;

/// <summary>
/// A single, one-time, idempotent deployment-time backfill of an
/// event-derived projection — see ADR-017 for the full rationale. Lives
/// exclusively in <c>IHostPro.MigrationRunner</c>: implementations are the
/// only place in the platform authorized to read a Bounded Context's source
/// schema to initialize another projection's data, and only at
/// deployment/upgrade time, never at runtime.
/// </summary>
public interface IProjectionBootstrapStep
{
    /// <summary>A short, log-friendly identifier for this step (e.g. the projection table it backfills).</summary>
    string Name { get; }

    Task ExecuteAsync(ILogger log, CancellationToken cancellationToken);
}
