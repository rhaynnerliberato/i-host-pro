using IHostPro.Contexts.Communication.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace IHostPro.Contexts.Communication.Infrastructure.Persistence.Mappings;

/// <summary>
/// <c>communication.administrator_notification_contacts</c> — tenant-owned,
/// RLS-protected (Fase 11, Checkpoint 6). Partial unique index on
/// <c>tenant_id</c> WHERE <c>is_active</c> enforces "at most one ACTIVE
/// contact per Tenant" (CP6 mandate item 20) at the database level —
/// defense-in-depth behind the Application-layer lookup-before-create.
/// </summary>
public sealed class AdministratorNotificationContactConfiguration : IEntityTypeConfiguration<AdministratorNotificationContact>
{
    public void Configure(EntityTypeBuilder<AdministratorNotificationContact> builder)
    {
        builder.ToTable("administrator_notification_contacts");

        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(c => c.TenantId).HasColumnName("tenant_id").IsRequired();
        builder.Property(c => c.DestinationPhone).HasColumnName("destination_phone").HasMaxLength(30).IsRequired();
        builder.Property(c => c.IsActive).HasColumnName("is_active").IsRequired();
        builder.Property(c => c.CreatedAtUtc).HasColumnName("created_at_utc").IsRequired();
        builder.Property(c => c.UpdatedAtUtc).HasColumnName("updated_at_utc").IsRequired();

        builder.HasIndex(c => c.TenantId, "ix_administrator_notification_contacts_active_per_tenant")
            .IsUnique()
            .HasFilter("is_active");
    }
}
