namespace IHostPro.Contexts.Identity.Infrastructure.Persistence;

/// <summary>
/// Keyless materialization shape for the single atomic
/// <c>UPDATE ... RETURNING</c> statement in <c>PasswordResetTokenRepository.ConsumeByTokenHashAsync</c>.
/// Never backed by a real table (configured with <c>.HasNoKey().ToView(null)</c>)
/// — exists purely so EF Core can map the RETURNING result set.
/// </summary>
internal sealed class PasswordResetTokenConsumptionRow
{
    public Guid TenantId { get; init; }
    public Guid UserId { get; init; }
}
