using IHostPro.Contexts.PropertyManagement.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace IHostPro.Contexts.PropertyManagement.Infrastructure.Persistence.Mappings;

/// <summary>
/// `property_management.condominiums` — tenant-owned, RLS-protected
/// (Checkpoint 0/1 plan). Address is always present (owned, required),
/// unlike <see cref="Property"/>'s optional own address.
/// </summary>
public sealed class CondominiumConfiguration : IEntityTypeConfiguration<Condominium>
{
    public void Configure(EntityTypeBuilder<Condominium> builder)
    {
        builder.ToTable("condominiums");

        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).HasColumnName("id").ValueGeneratedNever();
        builder.HasAlternateKey(c => new { c.TenantId, c.Id });

        builder.Property(c => c.TenantId).HasColumnName("tenant_id").IsRequired();
        builder.Property(c => c.Name).HasColumnName("name").HasMaxLength(200).IsRequired();

        builder.OwnsOne(c => c.Address, address =>
        {
            address.Property(a => a.ZipCode).HasColumnName("address_zip_code").HasMaxLength(8).IsRequired();
            address.Property(a => a.Street).HasColumnName("address_street").HasMaxLength(150).IsRequired();
            address.Property(a => a.Number).HasColumnName("address_number").HasMaxLength(20).IsRequired();
            address.Property(a => a.Complement).HasColumnName("address_complement").HasMaxLength(100);
            address.Property(a => a.Neighborhood).HasColumnName("address_neighborhood").HasMaxLength(100).IsRequired();
            address.Property(a => a.City).HasColumnName("address_city").HasMaxLength(100).IsRequired();
            address.Property(a => a.State).HasColumnName("address_state").HasMaxLength(2).IsRequired();
            address.Property(a => a.Country).HasColumnName("address_country").HasMaxLength(2).IsRequired();
        });
        builder.Navigation(c => c.Address).IsRequired();

        builder.Property(c => c.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(c => c.UpdatedAt).HasColumnName("updated_at").IsRequired();

        // Optimistic concurrency guard, mapped to PostgreSQL's built-in system
        // column `xmin` (Checkpoint 0/1 plan, item 6) — mirrors UserConfiguration.
        builder.Property<uint>("xmin").HasColumnName("xmin").IsRowVersion();

        builder.HasIndex(c => new { c.TenantId, c.Id });
    }
}
