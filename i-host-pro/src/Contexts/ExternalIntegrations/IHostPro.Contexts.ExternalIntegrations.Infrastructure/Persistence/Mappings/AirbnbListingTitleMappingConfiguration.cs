using IHostPro.Contexts.ExternalIntegrations.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace IHostPro.Contexts.ExternalIntegrations.Infrastructure.Persistence.Mappings;

/// <summary>
/// <c>external_integrations.airbnb_listing_title_mappings</c> — tenant-owned,
/// RLS-protected. Unique on (tenant_id, listing_title) — exact match only, no
/// physical foreign key to <c>property_management.properties</c>, mirrors
/// <c>AirbnbListingMappingConfiguration</c>'s own documented rationale.
/// </summary>
public sealed class AirbnbListingTitleMappingConfiguration : IEntityTypeConfiguration<AirbnbListingTitleMapping>
{
    public void Configure(EntityTypeBuilder<AirbnbListingTitleMapping> builder)
    {
        builder.ToTable("airbnb_listing_title_mappings");

        builder.HasKey(m => m.Id);
        builder.Property(m => m.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(m => m.TenantId).HasColumnName("tenant_id").IsRequired();
        builder.Property(m => m.ListingTitle).HasColumnName("listing_title").HasMaxLength(200).IsRequired();
        builder.Property(m => m.PropertyId).HasColumnName("property_id").IsRequired();
        builder.Property(m => m.CreatedAtUtc).HasColumnName("created_at_utc").IsRequired();
        builder.Property(m => m.UpdatedAtUtc).HasColumnName("updated_at_utc");

        builder.HasIndex(m => new { m.TenantId, m.ListingTitle }).IsUnique();
    }
}
