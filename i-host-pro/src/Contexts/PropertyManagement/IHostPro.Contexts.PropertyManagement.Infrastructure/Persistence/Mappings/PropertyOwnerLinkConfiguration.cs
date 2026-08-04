using IHostPro.Contexts.PropertyManagement.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace IHostPro.Contexts.PropertyManagement.Infrastructure.Persistence.Mappings;

/// <summary>
/// `property_management.property_owners` — tenant-owned, RLS-protected
/// (Checkpoint 0 plan, item 8). <c>owner_user_id</c>/<c>created_by_user_id</c>
/// are opaque references to Identity &amp; Access <c>User</c>s — deliberately
/// NOT foreign keys, since a physical FK across Bounded Context schemas would
/// violate the isolation "Architecture Principles.md" §7/§14 requires.
/// </summary>
public sealed class PropertyOwnerLinkConfiguration : IEntityTypeConfiguration<PropertyOwnerLink>
{
    public void Configure(EntityTypeBuilder<PropertyOwnerLink> builder)
    {
        builder.ToTable("property_owners");

        builder.HasKey(l => l.Id);
        builder.Property(l => l.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(l => l.TenantId).HasColumnName("tenant_id").IsRequired();
        builder.Property(l => l.PropertyId).HasColumnName("property_id").IsRequired();
        builder.Property(l => l.OwnerUserId).HasColumnName("owner_user_id").IsRequired();
        builder.Property(l => l.CreatedByUserId).HasColumnName("created_by_user_id").IsRequired();
        builder.Property(l => l.CreatedAt).HasColumnName("created_at").IsRequired();

        builder.HasOne<Property>()
            .WithMany()
            .HasForeignKey(l => new { l.TenantId, l.PropertyId })
            .HasPrincipalKey(p => new { p.TenantId, p.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(l => new { l.TenantId, l.PropertyId, l.OwnerUserId })
            .IsUnique()
            .HasDatabaseName("uq_property_owners_tenant_property_owner");

        builder.HasIndex(l => new { l.TenantId, l.OwnerUserId });
    }
}
