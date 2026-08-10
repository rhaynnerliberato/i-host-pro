using IHostPro.Contexts.Configuration.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace IHostPro.Contexts.Configuration.Infrastructure.Persistence.Mappings;

/// <summary>
/// <c>configuration.global_policy_values</c> — platform-wide, no
/// <c>tenant_id</c>, no Row-Level Security (Fase 5, Incremento 1 official
/// decisions §4). Deliberately not seeded with any row — remains empty
/// until a default value is explicitly approved for a future increment
/// (official decision 2.2).
/// </summary>
public sealed class GlobalPolicyValueConfiguration : IEntityTypeConfiguration<GlobalPolicyValue>
{
    public void Configure(EntityTypeBuilder<GlobalPolicyValue> builder)
    {
        builder.ToTable("global_policy_values");

        builder.HasKey(v => v.Id);
        builder.Property(v => v.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(v => v.PolicyCode).HasColumnName("policy_code").HasMaxLength(100).IsRequired();

        builder.Property(v => v.Value)
            .HasColumnName("value")
            .HasColumnType("jsonb")
            .IsRequired();

        builder.Property(v => v.CreatedAtUtc).HasColumnName("created_at_utc").IsRequired();

        builder.HasOne<PolicyDefinition>()
            .WithMany()
            .HasForeignKey(v => v.PolicyCode)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(v => v.PolicyCode).IsUnique();
    }
}
