using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace IHostPro.Contexts.ExternalIntegrations.Infrastructure.Persistence.Mappings;

/// <summary>
/// <c>external_integrations.airbnb_email_oauth_transactions</c> — see
/// <see cref="AirbnbEmailOAuthTransaction"/>'s remarks for why this table
/// deliberately has NO Row-Level Security policy (its migration must not add
/// one). <c>state_hash</c> — never the raw state value, see the Web OAuth
/// architecture gate item 11 — is the only lookup key the callback path uses.
/// </summary>
public sealed class AirbnbEmailOAuthTransactionConfiguration : IEntityTypeConfiguration<AirbnbEmailOAuthTransaction>
{
    public void Configure(EntityTypeBuilder<AirbnbEmailOAuthTransaction> builder)
    {
        builder.ToTable("airbnb_email_oauth_transactions");

        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(t => t.StateHash).HasColumnName("state_hash").HasMaxLength(64).IsRequired();
        builder.HasIndex(t => t.StateHash).IsUnique();

        builder.Property(t => t.TenantId).HasColumnName("tenant_id").IsRequired();
        builder.Property(t => t.ActorUserId).HasColumnName("actor_user_id").IsRequired();
        builder.Property(t => t.ProtectedPkceVerifier).HasColumnName("protected_pkce_verifier").HasColumnType("bytea").IsRequired();
        builder.Property(t => t.CreatedAtUtc).HasColumnName("created_at_utc").IsRequired();
        builder.Property(t => t.ExpiresAtUtc).HasColumnName("expires_at_utc").IsRequired();
        builder.Property(t => t.ConsumedAtUtc).HasColumnName("consumed_at_utc");
    }
}
