using IHostPro.Contexts.ExternalIntegrations.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace IHostPro.Contexts.ExternalIntegrations.Infrastructure.Persistence.Mappings;

/// <summary>
/// <c>external_integrations.whatsapp_tenant_routes</c> — deliberately global,
/// NOT tenant-owned, NOT RLS-protected (Fase 9, Checkpoint 2.3.2 — ADR-022
/// items 10/11/12). <see cref="WhatsAppTenantRoute"/> does not implement
/// <c>ITenantOwned</c>, so <c>BaseDbContext</c> never applies a tenant Global
/// Query Filter to it (see <c>BaseDbContext.OnModelCreating</c>) — this
/// table's migration also never calls <c>ENABLE ROW LEVEL SECURITY</c>. Both
/// facts are load-bearing: this table exists specifically to answer "which
/// tenant" BEFORE a <c>TenantId</c> is known, which an RLS-protected table
/// cannot do.
/// </summary>
public sealed class WhatsAppTenantRouteConfiguration : IEntityTypeConfiguration<WhatsAppTenantRoute>
{
    public void Configure(EntityTypeBuilder<WhatsAppTenantRoute> builder)
    {
        builder.ToTable("whatsapp_tenant_routes");

        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(r => r.PhoneNumberId).HasColumnName("phone_number_id").HasMaxLength(100).IsRequired();
        builder.Property(r => r.TenantId).HasColumnName("tenant_id").IsRequired();
        builder.Property(r => r.CreatedAtUtc).HasColumnName("created_at_utc").IsRequired();
        builder.Property(r => r.UpdatedAtUtc).HasColumnName("updated_at_utc");

        builder.HasIndex(r => r.PhoneNumberId).IsUnique();
        builder.HasIndex(r => r.TenantId).IsUnique();
    }
}
