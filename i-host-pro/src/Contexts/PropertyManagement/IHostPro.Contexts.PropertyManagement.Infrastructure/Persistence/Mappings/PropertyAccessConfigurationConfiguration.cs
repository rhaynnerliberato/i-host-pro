using IHostPro.Contexts.PropertyManagement.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace IHostPro.Contexts.PropertyManagement.Infrastructure.Persistence.Mappings;

/// <summary>
/// `property_management.property_access_configurations` — tenant-owned,
/// RLS-protected (Fase 10, Checkpoint 6.2 — Guest Access Secure Delivery).
/// Cardinality rule ("at most one row per Property") is a plain unique
/// constraint on <c>(tenant_id, property_id)</c> — never partial/filtered:
/// this entity is updated in place
/// (<see cref="PropertyAccessConfiguration.UpdateConfiguration"/>), never
/// soft-deleted and re-created, mirrors <c>FrontDeskContactConfiguration</c>
/// exactly. <see cref="PropertyAccessConfiguration.AccessCredentialSecretReference"/>
/// never holds a raw credential value — only an opaque reference string, so
/// it needs no special column-level protection beyond RLS/tenant isolation
/// (same as every other tenant-owned column).
/// </summary>
public sealed class PropertyAccessConfigurationConfiguration : IEntityTypeConfiguration<PropertyAccessConfiguration>
{
    public void Configure(EntityTypeBuilder<PropertyAccessConfiguration> builder)
    {
        builder.ToTable("property_access_configurations");

        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).HasColumnName("id").ValueGeneratedNever();
        builder.HasAlternateKey(c => new { c.TenantId, c.Id });

        builder.Property(c => c.TenantId).HasColumnName("tenant_id").IsRequired();
        builder.Property(c => c.PropertyId).HasColumnName("property_id").IsRequired();

        builder.HasOne<Property>()
            .WithMany()
            .HasForeignKey(c => new { c.TenantId, c.PropertyId })
            .HasPrincipalKey(p => new { p.TenantId, p.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.Property(c => c.AccessCredentialSecretReference).HasColumnName("access_credential_secret_reference").HasMaxLength(200);
        builder.Property(c => c.AccessInstructions).HasColumnName("access_instructions").HasColumnType("text");
        builder.Property(c => c.IsActive).HasColumnName("is_active").IsRequired();

        builder.Property(c => c.CreatedAtUtc).HasColumnName("created_at_utc").IsRequired();
        builder.Property(c => c.UpdatedAtUtc).HasColumnName("updated_at_utc").IsRequired();

        // Optimistic concurrency guard, mapped to PostgreSQL's built-in system
        // column `xmin` — mirrors CondominiumConfiguration/FrontDeskContactConfiguration.
        builder.Property<uint>("xmin").HasColumnName("xmin").IsRowVersion();

        builder.HasIndex(c => new { c.TenantId, c.PropertyId })
            .IsUnique()
            .HasDatabaseName("ix_property_access_configurations_tenant_id_property_id_unique");
    }
}
