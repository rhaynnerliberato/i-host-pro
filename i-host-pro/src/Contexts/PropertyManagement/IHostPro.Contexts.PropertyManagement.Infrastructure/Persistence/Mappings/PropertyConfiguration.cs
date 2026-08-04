using IHostPro.Contexts.PropertyManagement.Domain;
using IHostPro.Contexts.PropertyManagement.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace IHostPro.Contexts.PropertyManagement.Infrastructure.Persistence.Mappings;

/// <summary>
/// `property_management.properties` — tenant-owned, RLS-protected
/// (Checkpoint 0/1 plan). <see cref="Property.Code"/> is mapped as a single
/// converted scalar column plus a top-level, independently indexed
/// <see cref="Property.NormalizedCode"/> mirror — the same pattern
/// <c>UserConfiguration</c> uses for <c>Email</c>/<c>NormalizedEmail</c>,
/// since EF Core cannot translate a composite index spanning an owner
/// property (TenantId) and a member of a converted Value Object
/// (PropertyCode.NormalizedValue).
///
/// <c>ck_properties_effective_address_source</c> mirrors, at the database
/// level, the invariant <see cref="Property"/>'s constructor already
/// enforces in process (Checkpoint 0 plan, item 5): a Property with no
/// <see cref="Property.CondominiumId"/> must carry every required field of
/// its own address.
/// </summary>
public sealed class PropertyConfiguration : IEntityTypeConfiguration<Property>
{
    public void Configure(EntityTypeBuilder<Property> builder)
    {
        builder.ToTable("properties", table => table.HasCheckConstraint(
            "ck_properties_effective_address_source",
            """
            condominium_id IS NOT NULL OR (
                address_street IS NOT NULL AND
                address_number IS NOT NULL AND
                address_neighborhood IS NOT NULL AND
                address_city IS NOT NULL AND
                address_state IS NOT NULL AND
                address_zip_code IS NOT NULL AND
                address_country IS NOT NULL
            )
            """));

        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).HasColumnName("id").ValueGeneratedNever();
        builder.HasAlternateKey(p => new { p.TenantId, p.Id });

        builder.Property(p => p.TenantId).HasColumnName("tenant_id").IsRequired();

        builder.Property(p => p.Code)
            .HasConversion(code => code.Value, value => PropertyCode.Create(value))
            .HasColumnName("code")
            .HasMaxLength(50)
            .IsRequired();

        builder.Property(p => p.NormalizedCode).HasColumnName("normalized_code").HasMaxLength(50).IsRequired();
        builder.HasIndex(p => new { p.TenantId, p.NormalizedCode })
            .IsUnique()
            .HasDatabaseName("uq_properties_tenant_normalized_code");

        builder.Property(p => p.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
        builder.Property(p => p.Capacity).HasColumnName("capacity").IsRequired();
        builder.Property(p => p.CondominiumId).HasColumnName("condominium_id");

        builder.HasOne<Condominium>()
            .WithMany()
            .HasForeignKey(p => new { p.TenantId, p.CondominiumId })
            .HasPrincipalKey(c => new { c.TenantId, c.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.OwnsOne(p => p.Address, address =>
        {
            address.Property(a => a.ZipCode).HasColumnName("address_zip_code").HasMaxLength(8);
            address.Property(a => a.Street).HasColumnName("address_street").HasMaxLength(150);
            address.Property(a => a.Number).HasColumnName("address_number").HasMaxLength(20);
            address.Property(a => a.Complement).HasColumnName("address_complement").HasMaxLength(100);
            address.Property(a => a.Neighborhood).HasColumnName("address_neighborhood").HasMaxLength(100);
            address.Property(a => a.City).HasColumnName("address_city").HasMaxLength(100);
            address.Property(a => a.State).HasColumnName("address_state").HasMaxLength(2);
            address.Property(a => a.Country).HasColumnName("address_country").HasMaxLength(2);
        });

        builder.Property(p => p.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(20).IsRequired();

        builder.Property(p => p.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(p => p.UpdatedAt).HasColumnName("updated_at").IsRequired();

        // Optimistic concurrency guard, mapped to PostgreSQL's built-in system
        // column `xmin` (Checkpoint 0/1 plan, item 6) — mirrors UserConfiguration.
        builder.Property<uint>("xmin").HasColumnName("xmin").IsRowVersion();

        builder.HasIndex(p => new { p.TenantId, p.CondominiumId });
    }
}
