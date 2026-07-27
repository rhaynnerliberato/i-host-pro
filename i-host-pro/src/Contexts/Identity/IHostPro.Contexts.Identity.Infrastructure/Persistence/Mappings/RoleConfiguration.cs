using IHostPro.Contexts.Identity.Domain;
using IHostPro.Contexts.Identity.Infrastructure.Seed;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace IHostPro.Contexts.Identity.Infrastructure.Persistence.Mappings;

/// <summary>`identity.roles` — platform catalog, no Row-Level Security.</summary>
public sealed class RoleConfiguration : IEntityTypeConfiguration<Role>
{
    public void Configure(EntityTypeBuilder<Role> builder)
    {
        builder.ToTable("roles");

        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).HasColumnName("code").HasMaxLength(30).ValueGeneratedNever();

        builder.Property(r => r.DisplayName).HasColumnName("display_name").HasMaxLength(100).IsRequired();

        builder.HasData(IdentityCatalogSeed.Roles);
    }
}
