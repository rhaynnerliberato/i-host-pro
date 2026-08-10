using IHostPro.Contexts.Configuration.Domain;
using IHostPro.Contexts.Configuration.Infrastructure.Seed;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace IHostPro.Contexts.Configuration.Infrastructure.Persistence.Mappings;

/// <summary>`configuration.policy_definitions` — platform catalog, no Row-Level Security.</summary>
public sealed class PolicyDefinitionConfiguration : IEntityTypeConfiguration<PolicyDefinition>
{
    public void Configure(EntityTypeBuilder<PolicyDefinition> builder)
    {
        builder.ToTable("policy_definitions");

        builder.HasKey(d => d.Id);
        builder.Property(d => d.Id).HasColumnName("code").HasMaxLength(100).ValueGeneratedNever();

        builder.Property(d => d.Name).HasColumnName("name").HasMaxLength(150).IsRequired();
        builder.Property(d => d.Description).HasColumnName("description").HasMaxLength(1000).IsRequired();
        builder.Property(d => d.Category).HasColumnName("category").HasMaxLength(50).IsRequired();
        builder.Property(d => d.ValueType).HasColumnName("value_type").HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(d => d.SchemaVersion).HasColumnName("schema_version").IsRequired();
        builder.Property(d => d.IsActive).HasColumnName("is_active").IsRequired();

        builder.HasData(ConfigurationCatalogSeed.PolicyDefinitions);
    }
}
