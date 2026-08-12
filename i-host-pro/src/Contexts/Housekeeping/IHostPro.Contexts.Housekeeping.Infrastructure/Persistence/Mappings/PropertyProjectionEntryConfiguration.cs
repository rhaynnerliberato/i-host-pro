using IHostPro.Contexts.Housekeeping.Infrastructure.Projections;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace IHostPro.Contexts.Housekeeping.Infrastructure.Persistence.Mappings;

/// <summary>
/// <c>housekeeping.property_projection</c> — tenant-owned, RLS-protected
/// local read-model, built exclusively from Property Management's own
/// lifecycle events (Fase 6, Incremento 1, Checkpoint 0/3 gate).
/// </summary>
public sealed class PropertyProjectionEntryConfiguration : IEntityTypeConfiguration<PropertyProjectionEntry>
{
    public void Configure(EntityTypeBuilder<PropertyProjectionEntry> builder)
    {
        builder.ToTable("property_projection");

        builder.HasKey(p => new { p.TenantId, p.PropertyId });

        builder.Property(p => p.TenantId).HasColumnName("tenant_id").IsRequired();
        builder.Property(p => p.PropertyId).HasColumnName("property_id").IsRequired();
        builder.Property(p => p.IsActive).HasColumnName("is_active").IsRequired();
    }
}
