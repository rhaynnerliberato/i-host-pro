using System.Text.Json;
using IHostPro.Contexts.ExternalIntegrations.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace IHostPro.Contexts.ExternalIntegrations.Infrastructure.Persistence.Mappings;

/// <summary>
/// <c>external_integrations.whatsapp_template_mappings</c> — tenant-owned,
/// RLS-protected (Fase 9, Checkpoint 2.2). Unique on (tenant_id,
/// template_key) — enforced by a unique index, never just an
/// application-level check (mirrors <c>WhatsAppIntegrationConfiguration</c>'s
/// own precedent). <see cref="WhatsAppTemplateMapping.ParameterOrder"/> is
/// persisted as JSON — mirrors <c>PropertyAuditEntryConfiguration.ChangedFields</c>'s
/// own established <c>IReadOnlyList&lt;string&gt;</c> conversion exactly.
/// </summary>
public sealed class WhatsAppTemplateMappingConfiguration : IEntityTypeConfiguration<WhatsAppTemplateMapping>
{
    private static readonly JsonSerializerOptions JsonOptions = new();

    public void Configure(EntityTypeBuilder<WhatsAppTemplateMapping> builder)
    {
        builder.ToTable("whatsapp_template_mappings");

        builder.HasKey(m => m.Id);
        builder.Property(m => m.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(m => m.TenantId).HasColumnName("tenant_id").IsRequired();
        builder.Property(m => m.TemplateKey).HasColumnName("template_key").HasMaxLength(100).IsRequired();
        builder.Property(m => m.ProviderTemplateName).HasColumnName("provider_template_name").HasMaxLength(512).IsRequired();
        builder.Property(m => m.LanguageCode).HasColumnName("language_code").HasMaxLength(20).IsRequired();

        builder.Property(m => m.ParameterOrder)
            .HasConversion(
                order => JsonSerializer.Serialize(order, JsonOptions),
                json => JsonSerializer.Deserialize<IReadOnlyList<string>>(json, JsonOptions) ?? Array.Empty<string>())
            .HasColumnName("parameter_order")
            .HasColumnType("jsonb")
            .IsRequired();

        builder.Property(m => m.CreatedAtUtc).HasColumnName("created_at_utc").IsRequired();
        builder.Property(m => m.UpdatedAtUtc).HasColumnName("updated_at_utc");

        builder.HasIndex(m => new { m.TenantId, m.TemplateKey }).IsUnique();
    }
}
