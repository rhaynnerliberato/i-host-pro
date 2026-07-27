using IHostPro.Contexts.Identity.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace IHostPro.Contexts.Identity.Infrastructure.Persistence.Mappings;

/// <summary>
/// `identity.user_roles` — tenant-owned. Both foreign keys to `users` are
/// tenant-aware composites (Incremento 1 plan, Section 1): a role assignment
/// can never reference a user of a different tenant, and neither can the
/// optional "assigned by" reference (the FK is trivially satisfied when
/// `assigned_by_user_id` is null — Postgres `MATCH SIMPLE`, the default).
/// </summary>
public sealed class UserRoleConfiguration : IEntityTypeConfiguration<UserRole>
{
    public void Configure(EntityTypeBuilder<UserRole> builder)
    {
        builder.ToTable("user_roles");

        builder.HasKey(ur => new { ur.UserId, ur.RoleCode });

        builder.Property(ur => ur.TenantId).HasColumnName("tenant_id").IsRequired();
        builder.Property(ur => ur.UserId).HasColumnName("user_id").IsRequired();
        builder.Property(ur => ur.RoleCode).HasColumnName("role_code").HasMaxLength(30);
        builder.Property(ur => ur.AssignedAt).HasColumnName("assigned_at").IsRequired();
        builder.Property(ur => ur.AssignedByUserId).HasColumnName("assigned_by_user_id");

        builder.HasOne<Role>()
            .WithMany()
            .HasForeignKey(ur => ur.RoleCode)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(ur => new { ur.TenantId, ur.UserId })
            .HasPrincipalKey(u => new { u.TenantId, u.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(ur => new { ur.TenantId, ur.AssignedByUserId })
            .HasPrincipalKey(u => new { u.TenantId, u.Id })
            .OnDelete(DeleteBehavior.Restrict)
            .IsRequired(false);

        builder.HasIndex(ur => new { ur.TenantId, ur.UserId });
    }
}
