using IHostPro.Contexts.ExternalIntegrations.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace IHostPro.Contexts.ExternalIntegrations.Infrastructure.Persistence.Mappings;

/// <summary>
/// <c>external_integrations.airbnb_listing_mappings</c> — tenant-owned,
/// RLS-protected (Fase 9, Checkpoint 3.2). Unique on (tenant_id,
/// external_listing_id) — the same external listing id may map to different
/// properties for different tenants, but never twice for the same tenant.
/// <see cref="AirbnbListingMapping.PropertyId"/> carries no physical foreign
/// key to <c>property_management.properties</c> — mirrors
/// <c>ReservationConfiguration</c>'s own documented rationale for
/// <c>Reservation.PropertyId</c> exactly.
/// </summary>
public sealed class AirbnbListingMappingConfiguration : IEntityTypeConfiguration<AirbnbListingMapping>
{
    public void Configure(EntityTypeBuilder<AirbnbListingMapping> builder)
    {
        builder.ToTable("airbnb_listing_mappings");

        builder.HasKey(m => m.Id);
        builder.Property(m => m.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(m => m.TenantId).HasColumnName("tenant_id").IsRequired();
        builder.Property(m => m.AirbnbIntegrationId).HasColumnName("airbnb_integration_id").IsRequired();
        builder.Property(m => m.ExternalListingId).HasColumnName("external_listing_id").HasMaxLength(200).IsRequired();
        builder.Property(m => m.PropertyId).HasColumnName("property_id").IsRequired();
        builder.Property(m => m.CreatedAtUtc).HasColumnName("created_at_utc").IsRequired();
        builder.Property(m => m.UpdatedAtUtc).HasColumnName("updated_at_utc");

        builder.HasIndex(m => new { m.TenantId, m.ExternalListingId }).IsUnique();
    }
}
