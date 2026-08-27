using IHostPro.Contexts.PropertyManagement.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace IHostPro.Contexts.PropertyManagement.Infrastructure.Persistence.Mappings;

/// <summary>
/// `property_management.front_desk_contacts` — tenant-owned, RLS-protected
/// (Fase 10, Checkpoint 4). Cardinality rule ("at most one row per
/// Condominium") is a plain unique constraint on <c>(tenant_id,
/// condominium_id)</c> — never partial/filtered: this entity is updated in
/// place (<see cref="FrontDeskContact.UpdateContact"/>), never soft-deleted
/// and re-created, so there is never more than one row per Condominium
/// regardless of <see cref="FrontDeskContact.IsActive"/>.
/// </summary>
public sealed class FrontDeskContactConfiguration : IEntityTypeConfiguration<FrontDeskContact>
{
    public void Configure(EntityTypeBuilder<FrontDeskContact> builder)
    {
        builder.ToTable("front_desk_contacts");

        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).HasColumnName("id").ValueGeneratedNever();
        builder.HasAlternateKey(c => new { c.TenantId, c.Id });

        builder.Property(c => c.TenantId).HasColumnName("tenant_id").IsRequired();
        builder.Property(c => c.CondominiumId).HasColumnName("condominium_id").IsRequired();

        builder.HasOne<Condominium>()
            .WithMany()
            .HasForeignKey(c => new { c.TenantId, c.CondominiumId })
            .HasPrincipalKey(c => new { c.TenantId, c.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.Property(c => c.DisplayName).HasColumnName("display_name").HasMaxLength(200).IsRequired();
        builder.Property(c => c.PhoneNumber).HasColumnName("phone_number").HasMaxLength(30).IsRequired();
        builder.Property(c => c.IsActive).HasColumnName("is_active").IsRequired();

        builder.Property(c => c.CreatedAtUtc).HasColumnName("created_at_utc").IsRequired();
        builder.Property(c => c.UpdatedAtUtc).HasColumnName("updated_at_utc").IsRequired();

        // Optimistic concurrency guard, mapped to PostgreSQL's built-in system
        // column `xmin` — mirrors CondominiumConfiguration/PropertyConfiguration.
        builder.Property<uint>("xmin").HasColumnName("xmin").IsRowVersion();

        builder.HasIndex(c => new { c.TenantId, c.CondominiumId })
            .IsUnique()
            .HasDatabaseName("ix_front_desk_contacts_tenant_id_condominium_id_unique");
    }
}
