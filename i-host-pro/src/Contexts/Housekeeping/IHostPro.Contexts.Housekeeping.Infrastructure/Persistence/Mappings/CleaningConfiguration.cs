using IHostPro.Contexts.Housekeeping.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace IHostPro.Contexts.Housekeeping.Infrastructure.Persistence.Mappings;

/// <summary>
/// <c>housekeeping.cleanings</c> — tenant-owned, RLS-protected (Fase 6,
/// Incremento 1). <see cref="Cleaning.PropertyId"/>/<see cref="Cleaning.ReservationId"/>
/// carry NO physical foreign key to <c>property_management.properties</c>/
/// <c>reservations.reservations</c> (same opaque-Guid convention as
/// <c>Reservation.PropertyId</c>) — validated at write time via this
/// context's own local projections, never enforced by the database.
/// </summary>
public sealed class CleaningConfiguration : IEntityTypeConfiguration<Cleaning>
{
    public void Configure(EntityTypeBuilder<Cleaning> builder)
    {
        builder.ToTable("cleanings");

        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).HasColumnName("id").ValueGeneratedNever();
        builder.HasAlternateKey(c => new { c.TenantId, c.Id });

        builder.Property(c => c.TenantId).HasColumnName("tenant_id").IsRequired();
        builder.Property(c => c.PropertyId).HasColumnName("property_id").IsRequired();
        builder.Property(c => c.ReservationId).HasColumnName("reservation_id");
        builder.Property(c => c.AssignedHousekeeperUserId).HasColumnName("assigned_housekeeper_user_id");

        builder.Property(c => c.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(20).IsRequired();

        builder.Property(c => c.CreatedByUserId).HasColumnName("created_by_user_id");
        builder.Property(c => c.CreatedAtUtc).HasColumnName("created_at_utc").IsRequired();
        builder.Property(c => c.ScheduledAtUtc).HasColumnName("scheduled_at_utc");
        builder.Property(c => c.StartedAtUtc).HasColumnName("started_at_utc");
        builder.Property(c => c.InspectionStartedAtUtc).HasColumnName("inspection_started_at_utc");
        builder.Property(c => c.CompletedAtUtc).HasColumnName("completed_at_utc");
        builder.Property(c => c.CancelledAtUtc).HasColumnName("cancelled_at_utc");

        // Optimistic concurrency guard, mapped to PostgreSQL's built-in
        // system column `xmin` — mirrors ReservationConfiguration.
        builder.Property<uint>("xmin").HasColumnName("xmin").IsRowVersion();

        builder.HasIndex(c => new { c.TenantId, c.Status, c.CreatedAtUtc });
        builder.HasIndex(c => new { c.TenantId, c.PropertyId, c.CreatedAtUtc });
        builder.HasIndex(c => new { c.TenantId, c.AssignedHousekeeperUserId });

        // Backs ListForHousekeeperAsync's "Minhas Faxinas" ordering
        // (Fase 6, Incremento 2A) — ScheduledAtUtc ascending within a given
        // housekeeper/tenant.
        builder.HasIndex(c => new { c.TenantId, c.AssignedHousekeeperUserId, c.ScheduledAtUtc });

        // Non-unique — a Reservation may, in principle, be linked to more
        // than one Cleaning across its lifetime; the lookup used to react to
        // ReservationCancelled (Checkpoint 3) filters by status too.
        // The name MUST be passed to the HasIndex(expression, name) overload
        // itself (not chained on afterward via HasDatabaseName) — two
        // HasIndex(expression) calls over the identical property list
        // otherwise resolve to the SAME underlying index (EF Core looks up
        // an existing unnamed index over the same properties before
        // creating a new one), silently discarding whichever configuration
        // was applied first.
        builder.HasIndex(c => new { c.TenantId, c.ReservationId }, "ix_cleanings_tenant_id_reservation_id");

        // Idempotency guard for the automated Workflow -> Housekeeping flow
        // (Fase 8, Checkpoint 1 — ADR-018): CreatedByUserId == null marks a
        // Cleaning created by this flow. A partial unique index — never a
        // full unique index on (TenantId, ReservationId), which would break
        // the non-unique index above's own documented invariant that manual
        // Cleanings MAY repeat for the same Reservation — guarantees a
        // redelivered CreateCleaningForReservation can never create a
        // second automated Cleaning for the same Reservation, while never
        // constraining manual (CreatedByUserId != null) creations at all.
        builder.HasIndex(c => new { c.TenantId, c.ReservationId }, "ix_cleanings_tenant_id_reservation_id_automated_unique")
            .IsUnique()
            .HasFilter("\"created_by_user_id\" IS NULL");
    }
}
