using IHostPro.Contexts.Identity.Domain;
using IHostPro.Contexts.Identity.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace IHostPro.Contexts.Identity.Infrastructure.Persistence.Mappings;

/// <summary>
/// `identity.tenants` — platform-level table, deliberately NOT Row-Level
/// Security protected (Incremento 1 plan, "Tenant e RLS"): it is the tenant
/// boundary itself, not data owned by a tenant.
/// </summary>
public sealed class TenantConfiguration : IEntityTypeConfiguration<Tenant>
{
    public void Configure(EntityTypeBuilder<Tenant> builder)
    {
        builder.ToTable("tenants");

        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(t => t.Slug)
            .HasConversion(slug => slug.Value, value => TenantSlug.Create(value))
            .HasColumnName("slug")
            .HasMaxLength(63)
            .IsRequired();
        builder.HasIndex(t => t.Slug).IsUnique();

        builder.Property(t => t.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
        builder.Property(t => t.Status).HasColumnName("status").IsRequired();
        builder.Property(t => t.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(t => t.ActivatedAt).HasColumnName("activated_at").IsRequired();
    }
}
