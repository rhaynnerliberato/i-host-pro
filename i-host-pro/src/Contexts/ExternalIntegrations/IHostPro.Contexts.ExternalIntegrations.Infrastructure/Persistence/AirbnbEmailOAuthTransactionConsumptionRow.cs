namespace IHostPro.Contexts.ExternalIntegrations.Infrastructure.Persistence;

/// <summary>
/// Keyless materialization shape for the single atomic
/// <c>UPDATE ... RETURNING</c> statement in <c>AirbnbEmailOAuthTransactionRepository.ConsumeByStateHashAsync</c>.
/// Never backed by a real table (configured with <c>.HasNoKey().ToView(null)</c>)
/// — exists purely so EF Core can map the RETURNING result set.
/// </summary>
internal sealed class AirbnbEmailOAuthTransactionConsumptionRow
{
    public Guid TenantId { get; init; }
    public Guid ActorUserId { get; init; }
    public byte[] ProtectedPkceVerifier { get; init; } = null!;
}
