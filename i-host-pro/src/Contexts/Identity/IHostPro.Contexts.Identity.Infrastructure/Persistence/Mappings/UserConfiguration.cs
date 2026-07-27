using IHostPro.Contexts.Identity.Domain;
using IHostPro.Contexts.Identity.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace IHostPro.Contexts.Identity.Infrastructure.Persistence.Mappings;

/// <summary>
/// `identity.users` — the single source of truth for an authenticated user
/// (Incremento 1 plan, Section 2): no separate ASP.NET Core Identity
/// `AspNetUsers` table exists. Email doubles as the Identity "username"
/// (Section 2). The `(tenant_id, id)` alternate key is what lets other tables
/// declare tenant-aware composite foreign keys against this table
/// (Incremento 1 plan, Section 1).
/// </summary>
public sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("users");

        builder.HasKey(u => u.Id);
        builder.Property(u => u.Id).HasColumnName("id").ValueGeneratedNever();
        builder.HasAlternateKey(u => new { u.TenantId, u.Id });

        builder.Property(u => u.TenantId).HasColumnName("tenant_id").IsRequired();

        // A user can never reference a tenant that does not exist — the root
        // of every tenant-aware constraint chain in this schema (adendo
        // final review: this FK was missing from the original Incremento 1
        // migration; user_roles/sessions/refresh_tokens FKs alone do not
        // guarantee users.tenant_id itself points at a real tenant).
        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(u => u.TenantId)
            .OnDelete(DeleteBehavior.Restrict);

        // Email is mapped as a single converted scalar column (not an owned
        // type): EF Core cannot translate a composite index spanning an owner
        // property (TenantId) and a member of an owned/converted Value Object
        // (Email.NormalizedValue). NormalizedEmail is therefore a top-level,
        // independently mapped mirror of Email.NormalizedValue, kept in sync
        // exclusively by User.ChangeEmail (see User.cs).
        builder.Property(u => u.Email)
            .HasConversion(email => email.Value, value => Email.Create(value))
            .HasColumnName("email")
            .HasMaxLength(320)
            .IsRequired();

        builder.Property(u => u.NormalizedEmail).HasColumnName("normalized_email").HasMaxLength(320).IsRequired();
        builder.HasIndex(u => new { u.TenantId, u.NormalizedEmail }).IsUnique();

        builder.Property(u => u.FullName).HasColumnName("full_name").HasMaxLength(200).IsRequired();

        builder.Property(u => u.PasswordHash)
            .HasConversion(hash => hash.Value, value => PasswordHash.FromEncoded(value))
            .HasColumnName("password_hash")
            .IsRequired();

        builder.Property(u => u.SecurityStamp).HasColumnName("security_stamp").HasMaxLength(64).IsRequired();
        builder.Property(u => u.Status).HasColumnName("status").IsRequired();
        builder.Property(u => u.FailedAccessCount).HasColumnName("failed_access_count").IsRequired();
        builder.Property(u => u.LockoutEnd).HasColumnName("lockout_end");
        builder.Property(u => u.LastLoginAt).HasColumnName("last_login_at");
        builder.Property(u => u.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(u => u.UpdatedAt).HasColumnName("updated_at").IsRequired();

        // Optimistic concurrency guard, mapped to PostgreSQL's built-in system
        // column `xmin` (Incremento 1 plan, Section 3/5 — concurrency handling).
        builder.Property<uint>("xmin").HasColumnName("xmin").IsRowVersion();
    }
}
