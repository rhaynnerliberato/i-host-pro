using IHostPro.Contexts.Identity.Domain;
using IHostPro.Contexts.Identity.Infrastructure.Seed;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace IHostPro.Contexts.Identity.Infrastructure.Persistence.Mappings;

/// <summary>`identity.permissions` — platform catalog, no Row-Level Security.</summary>
public sealed class PermissionConfiguration : IEntityTypeConfiguration<Permission>
{
    public void Configure(EntityTypeBuilder<Permission> builder)
    {
        builder.ToTable("permissions");

        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).HasColumnName("code").HasMaxLength(100).ValueGeneratedNever();

        builder.Property(p => p.Resource).HasColumnName("resource").HasMaxLength(50).IsRequired();
        builder.Property(p => p.Action).HasColumnName("action").HasMaxLength(50).IsRequired();
        builder.Property(p => p.Scope).HasColumnName("scope").HasMaxLength(50);

        builder.HasData(IdentityCatalogSeed.Permissions);
    }
}
