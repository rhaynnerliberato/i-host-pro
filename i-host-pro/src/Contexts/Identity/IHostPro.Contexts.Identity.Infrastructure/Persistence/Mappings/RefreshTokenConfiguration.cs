using IHostPro.Contexts.Identity.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace IHostPro.Contexts.Identity.Infrastructure.Persistence.Mappings;

/// <summary>
/// `identity.refresh_tokens` — tenant-owned. `token_id` is the public,
/// globally unique selector embedded in the opaque token string (a random
/// UUID, collision-negligible — a global unique index is simpler and just as
/// correct as a tenant-composite one, Incremento 1 plan, Section 6).
/// `token_hash` is the SHA-256 of the entire presented token string, never
/// just the secret portion. The foreign key to `sessions` is a tenant-aware
/// composite that also pins the owning user (Incremento 1 plan, Section 1).
/// </summary>
public sealed class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> builder)
    {
        builder.ToTable("refresh_tokens");

        builder.HasKey(rt => rt.Id);
        builder.Property(rt => rt.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(rt => rt.TokenId).HasColumnName("token_id").IsRequired();
        builder.HasIndex(rt => rt.TokenId).IsUnique();

        builder.Property(rt => rt.TenantId).HasColumnName("tenant_id").IsRequired();
        builder.Property(rt => rt.SessionId).HasColumnName("session_id").IsRequired();
        builder.Property(rt => rt.UserId).HasColumnName("user_id").IsRequired();
        builder.Property(rt => rt.TokenHash).HasColumnName("token_hash").HasMaxLength(64).IsRequired();
        builder.Property(rt => rt.IssuedAt).HasColumnName("issued_at").IsRequired();
        builder.Property(rt => rt.ExpiresAt).HasColumnName("expires_at").IsRequired();
        builder.Property(rt => rt.RevokedAt).HasColumnName("revoked_at");
        builder.Property(rt => rt.RevocationReason).HasColumnName("revocation_reason");
        builder.Property(rt => rt.PreviousTokenId).HasColumnName("previous_token_id");
        builder.Property(rt => rt.ReplacedByTokenId).HasColumnName("replaced_by_token_id");

        builder.HasOne<Session>()
            .WithMany()
            .HasForeignKey(rt => new { rt.TenantId, rt.SessionId, rt.UserId })
            .HasPrincipalKey(s => new { s.TenantId, s.Id, s.UserId })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(rt => new { rt.TenantId, rt.UserId });

        // Single source of truth for the rotation-race scenario (Incremento 1
        // plan, Section 7): PostgreSQL's built-in system column `xmin`,
        // surfaced by EF Core as a concurrency token, IS the "conditional
        // atomic UPDATE with affected-rows check" — there is no second,
        // hand-rolled mechanism.
        builder.Property<uint>("xmin").HasColumnName("xmin").IsRowVersion();
    }
}
