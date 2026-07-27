using IHostPro.Contexts.Identity.Domain;
using IHostPro.Contexts.Identity.Infrastructure.Seed;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace IHostPro.Contexts.Identity.Infrastructure.Persistence.Mappings;

/// <summary>`identity.role_permissions` — platform catalog, no Row-Level Security.</summary>
public sealed class RolePermissionConfiguration : IEntityTypeConfiguration<RolePermission>
{
    public void Configure(EntityTypeBuilder<RolePermission> builder)
    {
        builder.ToTable("role_permissions");

        builder.HasKey(rp => new { rp.RoleCode, rp.PermissionCode });
        builder.Property(rp => rp.RoleCode).HasColumnName("role_code").HasMaxLength(30);
        builder.Property(rp => rp.PermissionCode).HasColumnName("permission_code").HasMaxLength(100);

        builder.HasOne<Role>()
            .WithMany()
            .HasForeignKey(rp => rp.RoleCode)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Permission>()
            .WithMany()
            .HasForeignKey(rp => rp.PermissionCode)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasData(IdentityCatalogSeed.RolePermissions);
    }
}
