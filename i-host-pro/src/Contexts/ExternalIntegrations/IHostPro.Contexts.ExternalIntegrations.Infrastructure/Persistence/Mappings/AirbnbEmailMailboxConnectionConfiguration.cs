using IHostPro.Contexts.ExternalIntegrations.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace IHostPro.Contexts.ExternalIntegrations.Infrastructure.Persistence.Mappings;

/// <summary>
/// <c>external_integrations.airbnb_email_mailbox_connections</c> —
/// tenant-owned, RLS-protected. Exactly one row per <c>tenant_id</c> — an
/// explicit MVP constraint (one Airbnb email mailbox per tenant), enforced by
/// a unique index, mirrors <see cref="AirbnbIntegrationConfiguration"/>.
///
/// <c>xmin</c> is mapped as a concurrency token (mirrors
/// <c>PropertyConfiguration</c>/<c>UserConfiguration</c>): both the OAuth
/// callback and the Worker's background polling can write
/// <c>token_cache_blob</c>, and a lost update there would silently discard a
/// newer MSAL-refreshed cache.
/// </summary>
public sealed class AirbnbEmailMailboxConnectionConfiguration : IEntityTypeConfiguration<AirbnbEmailMailboxConnection>
{
    public void Configure(EntityTypeBuilder<AirbnbEmailMailboxConnection> builder)
    {
        builder.ToTable("airbnb_email_mailbox_connections");

        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(c => c.TenantId).HasColumnName("tenant_id").IsRequired();
        builder.Property(c => c.MailboxAddress).HasColumnName("mailbox_address").HasMaxLength(320);
        builder.Property(c => c.HomeAccountId).HasColumnName("home_account_id").HasMaxLength(200);
        builder.Property(c => c.AccountTenantId).HasColumnName("account_tenant_id").HasMaxLength(200);
        builder.Property(c => c.IsEnabled).HasColumnName("is_enabled").IsRequired();
        builder.Property(c => c.GrantedScopes).HasColumnName("granted_scopes").HasMaxLength(500);
        builder.Property(c => c.TokenCacheBlob).HasColumnName("token_cache_blob").HasColumnType("bytea");
        builder.Property(c => c.AuthorizationStatus)
            .HasColumnName("authorization_status")
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();
        builder.Property(c => c.LastAuthenticatedAtUtc).HasColumnName("last_authenticated_at_utc");
        builder.Property(c => c.CreatedAtUtc).HasColumnName("created_at_utc").IsRequired();
        builder.Property(c => c.UpdatedAtUtc).HasColumnName("updated_at_utc");

        builder.HasIndex(c => c.TenantId).IsUnique();

        // PostgreSQL's native row-version column (Checkpoint 0/1 plan, item 6) —
        // mirrors PropertyConfiguration/UserConfiguration exactly.
        builder.Property<uint>("xmin").HasColumnName("xmin").IsRowVersion();
    }
}
