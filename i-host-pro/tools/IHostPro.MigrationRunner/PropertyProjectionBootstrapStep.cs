using Microsoft.Extensions.Logging;

/// <summary>
/// Adapts the pre-existing <see cref="PropertyProjectionBootstrap"/> (Fase 7,
/// Incremento 1, Checkpoint 3) to the <see cref="IProjectionBootstrapStep"/>
/// shape introduced by ADR-017 — delegates to the same static method,
/// unchanged, never altering its semantics or SQL.
/// </summary>
public sealed class PropertyProjectionBootstrapStep : IProjectionBootstrapStep
{
    private readonly string _housekeepingConnectionString;

    public PropertyProjectionBootstrapStep(string housekeepingConnectionString) =>
        _housekeepingConnectionString = housekeepingConnectionString;

    public string Name => "housekeeping.property_projection";

    public Task ExecuteAsync(ILogger log, CancellationToken cancellationToken) =>
        PropertyProjectionBootstrap.RunAsync(_housekeepingConnectionString, log, cancellationToken);
}
