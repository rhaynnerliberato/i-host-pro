using IHostPro.Contexts.Configuration.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace IHostPro.Contexts.Configuration.Infrastructure.Persistence.Mappings;

/// <summary>
/// <c>configuration.templates</c> — tenant-owned, RLS-protected (Fase 9,
/// Checkpoint 1). Exactly one row per (<c>tenant_id</c>, <c>key</c>) —
/// enforced by a unique index, never just an application-level check —
/// keeps "the active template for this key" deterministic.
/// </summary>
public sealed class TemplateConfiguration : IEntityTypeConfiguration<Template>
{
    public void Configure(EntityTypeBuilder<Template> builder)
    {
        builder.ToTable("templates");

        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(t => t.TenantId).HasColumnName("tenant_id").IsRequired();
        builder.Property(t => t.Key).HasColumnName("key").HasMaxLength(100).IsRequired();
        builder.Property(t => t.Content).HasColumnName("content").HasColumnType("text").IsRequired();
        builder.Property(t => t.IsActive).HasColumnName("is_active").IsRequired();
        builder.Property(t => t.CreatedAtUtc).HasColumnName("created_at_utc").IsRequired();
        builder.Property(t => t.UpdatedAtUtc).HasColumnName("updated_at_utc");

        builder.HasIndex(t => new { t.TenantId, t.Key }).IsUnique();

        // Read path: resolving the active template for a given key.
        builder.HasIndex(t => new { t.TenantId, t.Key, t.IsActive });
    }
}
