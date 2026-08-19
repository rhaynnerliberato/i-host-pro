using IHostPro.Contexts.ExternalIntegrations.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace IHostPro.Contexts.ExternalIntegrations.Infrastructure.Persistence.Mappings;

/// <summary>
/// <c>external_integrations.whatsapp_integrations</c> — tenant-owned,
/// RLS-protected (Fase 9, Checkpoint 2.1). Exactly one row per
/// <c>tenant_id</c> — enforced by a unique index, never just an
/// application-level check (CP2.1 mandate §15: one WhatsApp integration per
/// tenant in the MVP). Never a column for a secret value itself — only
/// opaque secret references (CP2.1 mandate §14).
/// </summary>
public sealed class WhatsAppIntegrationConfiguration : IEntityTypeConfiguration<WhatsAppIntegration>
{
    public void Configure(EntityTypeBuilder<WhatsAppIntegration> builder)
    {
        builder.ToTable("whatsapp_integrations");

        builder.HasKey(w => w.Id);
        builder.Property(w => w.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(w => w.TenantId).HasColumnName("tenant_id").IsRequired();
        builder.Property(w => w.WabaId).HasColumnName("waba_id").HasMaxLength(100);
        builder.Property(w => w.PhoneNumberId).HasColumnName("phone_number_id").HasMaxLength(100);
        builder.Property(w => w.IsEnabled).HasColumnName("is_enabled").IsRequired();
        builder.Property(w => w.AccessTokenSecretReference).HasColumnName("access_token_secret_reference").HasMaxLength(200);
        builder.Property(w => w.AppSecretSecretReference).HasColumnName("app_secret_secret_reference").HasMaxLength(200);
        builder.Property(w => w.VerifyTokenSecretReference).HasColumnName("verify_token_secret_reference").HasMaxLength(200);
        builder.Property(w => w.CreatedAtUtc).HasColumnName("created_at_utc").IsRequired();
        builder.Property(w => w.UpdatedAtUtc).HasColumnName("updated_at_utc");

        builder.HasIndex(w => w.TenantId).IsUnique();
    }
}
