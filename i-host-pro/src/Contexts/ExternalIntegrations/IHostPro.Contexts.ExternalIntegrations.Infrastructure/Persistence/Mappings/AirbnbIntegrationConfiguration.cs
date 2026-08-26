using IHostPro.Contexts.ExternalIntegrations.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace IHostPro.Contexts.ExternalIntegrations.Infrastructure.Persistence.Mappings;

/// <summary>
/// <c>external_integrations.airbnb_integrations</c> — tenant-owned,
/// RLS-protected (Fase 9, Checkpoint 3.2). Exactly one row per
/// <c>tenant_id</c> — enforced by a unique index, mirrors
/// <c>WhatsAppIntegrationConfiguration</c> exactly.
/// </summary>
public sealed class AirbnbIntegrationConfiguration : IEntityTypeConfiguration<AirbnbIntegration>
{
    public void Configure(EntityTypeBuilder<AirbnbIntegration> builder)
    {
        builder.ToTable("airbnb_integrations");

        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(a => a.TenantId).HasColumnName("tenant_id").IsRequired();
        builder.Property(a => a.ExternalAccountId).HasColumnName("external_account_id").HasMaxLength(200);
        builder.Property(a => a.IsEnabled).HasColumnName("is_enabled").IsRequired();
        builder.Property(a => a.CredentialSecretReference).HasColumnName("credential_secret_reference").HasMaxLength(200);
        builder.Property(a => a.CreatedAtUtc).HasColumnName("created_at_utc").IsRequired();
        builder.Property(a => a.UpdatedAtUtc).HasColumnName("updated_at_utc");

        builder.HasIndex(a => a.TenantId).IsUnique();
    }
}
