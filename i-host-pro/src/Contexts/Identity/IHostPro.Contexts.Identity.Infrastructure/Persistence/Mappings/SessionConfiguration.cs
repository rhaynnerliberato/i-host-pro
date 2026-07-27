using IHostPro.Contexts.Identity.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace IHostPro.Contexts.Identity.Infrastructure.Persistence.Mappings;

/// <summary>
/// `identity.sessions` — tenant-owned. The `(tenant_id, id, user_id)`
/// alternate key is what lets `refresh_tokens` declare a tenant-aware
/// composite foreign key that also pins the session's owning user
/// (Incremento 1 plan, Section 1).
/// </summary>
public sealed class SessionConfiguration : IEntityTypeConfiguration<Session>
{
    public void Configure(EntityTypeBuilder<Session> builder)
    {
        builder.ToTable("sessions");

        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).HasColumnName("id").ValueGeneratedNever();
        builder.HasAlternateKey(s => new { s.TenantId, s.Id, s.UserId });

        builder.Property(s => s.TenantId).HasColumnName("tenant_id").IsRequired();
        builder.Property(s => s.UserId).HasColumnName("user_id").IsRequired();
        builder.Property(s => s.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(s => s.LastActivityAt).HasColumnName("last_activity_at").IsRequired();
        builder.Property(s => s.Device).HasColumnName("device").HasMaxLength(200);
        builder.Property(s => s.Browser).HasColumnName("browser").HasMaxLength(200);
        builder.Property(s => s.IpAddress).HasColumnName("ip_address").HasMaxLength(45);
        builder.Property(s => s.ApproximateLocation).HasColumnName("approximate_location").HasMaxLength(200);
        builder.Property(s => s.Status).HasColumnName("status").IsRequired();
        builder.Property(s => s.RevokedAt).HasColumnName("revoked_at");
        builder.Property(s => s.RevocationReason).HasColumnName("revocation_reason").HasMaxLength(50);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(s => new { s.TenantId, s.UserId })
            .HasPrincipalKey(u => new { u.TenantId, u.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(s => new { s.TenantId, s.UserId });
    }
}
