using FluentAssertions;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.Contexts.Dashboard.Infrastructure.Persistence;
using IHostPro.Contexts.Dashboard.Infrastructure.Projections;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;

namespace IHostPro.Contexts.Dashboard.Tests.Integration;

/// <summary>
/// Real-Postgres coverage for <see cref="DashboardOverviewReader"/> (Fase 7,
/// Incremento 2 — Dashboard &amp; Reporting Foundation, Checkpoint 2) —
/// proves the exact <c>[From, To)</c> boundary semantics, current-state vs.
/// period-filtered distinctions, the Delayed rule, and tenant isolation the
/// mandate specifies. Seeds <see cref="DashboardDbContext"/>'s own projection
/// rows directly (never Wolverine/events — the synchronizer's own correctness
/// is already covered by <c>DashboardReservationProjectionSynchronizerTests</c>).
/// <c>nowUtc</c> is always passed explicitly to the reader, so no
/// <c>TimeProvider</c> fake is needed here.
/// </summary>
public class DashboardOverviewReaderTests : IClassFixture<DashboardOverviewReaderTests.Fixture>
{
    private readonly Fixture _fixture;

    public DashboardOverviewReaderTests(Fixture fixture) => _fixture = fixture;

    // ---- Reservations: CheckIns/CheckOuts boundary -----------------------

    [Fact]
    public async Task CheckIn_exactly_at_From_is_included_exactly_at_To_is_excluded()
    {
        var tenantId = Guid.NewGuid();
        var from = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(2026, 3, 31, 0, 0, 0, TimeSpan.Zero);

        await SeedReservationAsync(tenantId, "confirmed", checkInAt: from, checkOutAt: from.AddDays(3));
        await SeedReservationAsync(tenantId, "confirmed", checkInAt: to, checkOutAt: to.AddDays(3));

        var overview = await GetOverviewAsync(tenantId, from, to, from);

        overview.Reservations.CheckInsInPeriod.Should().Be(1);
    }

    [Fact]
    public async Task CheckOut_exactly_at_From_is_included_exactly_at_To_is_excluded()
    {
        var tenantId = Guid.NewGuid();
        var from = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(2026, 3, 31, 0, 0, 0, TimeSpan.Zero);

        await SeedReservationAsync(tenantId, "confirmed", checkInAt: from.AddDays(-3), checkOutAt: from);
        await SeedReservationAsync(tenantId, "confirmed", checkInAt: to.AddDays(-3), checkOutAt: to);

        var overview = await GetOverviewAsync(tenantId, from, to, from);

        overview.Reservations.CheckOutsInPeriod.Should().Be(1);
    }

    [Fact]
    public async Task A_cancelled_reservation_never_counts_as_a_check_in_even_with_CheckInAt_in_period()
    {
        var tenantId = Guid.NewGuid();
        var from = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(2026, 3, 31, 0, 0, 0, TimeSpan.Zero);

        await SeedReservationAsync(tenantId, "cancelled", checkInAt: from.AddDays(5), checkOutAt: from.AddDays(8));

        var overview = await GetOverviewAsync(tenantId, from, to, from);

        overview.Reservations.CheckInsInPeriod.Should().Be(0);
    }

    [Fact]
    public async Task FutureReservations_counts_CheckInAt_greater_or_equal_to_nowUtc_excluding_cancelled()
    {
        var tenantId = Guid.NewGuid();
        var nowUtc = new DateTimeOffset(2026, 3, 15, 0, 0, 0, TimeSpan.Zero);

        await SeedReservationAsync(tenantId, "confirmed", checkInAt: nowUtc, checkOutAt: nowUtc.AddDays(2)); // future, at boundary
        await SeedReservationAsync(tenantId, "confirmed", checkInAt: nowUtc.AddDays(-1), checkOutAt: nowUtc.AddDays(1)); // past
        await SeedReservationAsync(tenantId, "cancelled", checkInAt: nowUtc.AddDays(10), checkOutAt: nowUtc.AddDays(12)); // future but cancelled

        var overview = await GetOverviewAsync(tenantId, nowUtc.AddDays(-30), nowUtc.AddDays(30), nowUtc);

        overview.Reservations.FutureReservations.Should().Be(1);
    }

    [Fact]
    public async Task CancelledInPeriod_uses_CancelledAtUtc_boundary_not_CheckInAt()
    {
        var tenantId = Guid.NewGuid();
        var from = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(2026, 3, 31, 0, 0, 0, TimeSpan.Zero);

        // CheckInAt far outside the window, but cancelled inside it.
        await SeedCancelledReservationAsync(
            tenantId, checkInAt: from.AddYears(1), checkOutAt: from.AddYears(1).AddDays(2), cancelledAtUtc: from.AddDays(5));

        // Cancelled outside the window entirely.
        await SeedCancelledReservationAsync(
            tenantId, checkInAt: from.AddDays(5), checkOutAt: from.AddDays(7), cancelledAtUtc: to.AddDays(5));

        var overview = await GetOverviewAsync(tenantId, from, to, from);

        overview.Reservations.CancelledInPeriod.Should().Be(1);
    }

    [Fact]
    public async Task StatusCounts_is_current_state_over_every_reservation_never_period_filtered()
    {
        var tenantId = Guid.NewGuid();
        var farFuture = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);

        await SeedReservationAsync(tenantId, "confirmed", checkInAt: farFuture, checkOutAt: farFuture.AddDays(2));
        await SeedReservationAsync(tenantId, "confirmed", checkInAt: farFuture, checkOutAt: farFuture.AddDays(2));
        await SeedCancelledReservationAsync(tenantId, farFuture, farFuture.AddDays(2), farFuture.AddDays(-100));

        // Query a period far away from every seeded row's own dates.
        var overview = await GetOverviewAsync(
            tenantId, new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2020, 2, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero));

        overview.Reservations.StatusCounts.Should().BeEquivalentTo(
        [
            new { Status = "confirmed", Count = 2 },
            new { Status = "cancelled", Count = 1 },
        ]);
    }

    // ---- Housekeeping ------------------------------------------------------

    [Theory]
    [InlineData("Pending")]
    [InlineData("Assigned")]
    public async Task Pending_counts_Pending_and_Assigned(string status)
    {
        var tenantId = Guid.NewGuid();
        await SeedCleaningAsync(tenantId, status, scheduledAtUtc: null);

        var overview = await GetOverviewAsync(tenantId);

        overview.Housekeeping.Pending.Should().Be(1);
    }

    [Theory]
    [InlineData("InTransit")]
    [InlineData("Started")]
    [InlineData("InInspection")]
    [InlineData("WaitingHelp")]
    [InlineData("WaitingMaterials")]
    public async Task InProgress_counts_the_five_active_statuses(string status)
    {
        var tenantId = Guid.NewGuid();
        await SeedCleaningAsync(tenantId, status, scheduledAtUtc: null);

        var overview = await GetOverviewAsync(tenantId);

        overview.Housekeeping.InProgress.Should().Be(1);
    }

    [Fact]
    public async Task Interrupted_counts_only_Interrupted()
    {
        var tenantId = Guid.NewGuid();
        await SeedCleaningAsync(tenantId, "Interrupted", scheduledAtUtc: null);

        var overview = await GetOverviewAsync(tenantId);

        overview.Housekeeping.Interrupted.Should().Be(1);
        overview.Housekeeping.InProgress.Should().Be(0);
        overview.Housekeeping.Pending.Should().Be(0);
    }

    [Fact]
    public async Task CompletedInPeriod_boundary_is_half_open()
    {
        var tenantId = Guid.NewGuid();
        var from = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(2026, 3, 31, 0, 0, 0, TimeSpan.Zero);

        await SeedCompletedCleaningAsync(tenantId, completedAtUtc: from);
        await SeedCompletedCleaningAsync(tenantId, completedAtUtc: to);

        var overview = await GetOverviewAsync(tenantId, from, to, from);

        overview.Housekeeping.CompletedInPeriod.Should().Be(1);
    }

    [Fact]
    public async Task CancelledInPeriod_boundary_is_half_open()
    {
        var tenantId = Guid.NewGuid();
        var from = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(2026, 3, 31, 0, 0, 0, TimeSpan.Zero);

        await SeedCancelledCleaningAsync(tenantId, cancelledAtUtc: from);
        await SeedCancelledCleaningAsync(tenantId, cancelledAtUtc: to);

        var overview = await GetOverviewAsync(tenantId, from, to, from);

        overview.Housekeeping.CancelledInPeriod.Should().Be(1);
    }

    [Fact]
    public async Task Delayed_is_true_when_ScheduledAtUtc_is_before_nowUtc_and_status_is_not_terminal()
    {
        var tenantId = Guid.NewGuid();
        var nowUtc = new DateTimeOffset(2026, 3, 15, 0, 0, 0, TimeSpan.Zero);

        await SeedCleaningAsync(tenantId, "Assigned", scheduledAtUtc: nowUtc.AddDays(-1));

        var overview = await GetOverviewAsync(tenantId, nowUtc.AddDays(-30), nowUtc.AddDays(30), nowUtc);

        overview.Housekeeping.Delayed.Should().Be(1);
    }

    [Fact]
    public async Task Delayed_is_false_when_ScheduledAtUtc_is_null()
    {
        var tenantId = Guid.NewGuid();
        var nowUtc = new DateTimeOffset(2026, 3, 15, 0, 0, 0, TimeSpan.Zero);

        await SeedCleaningAsync(tenantId, "Pending", scheduledAtUtc: null);

        var overview = await GetOverviewAsync(tenantId, nowUtc.AddDays(-30), nowUtc.AddDays(30), nowUtc);

        overview.Housekeeping.Delayed.Should().Be(0);
    }

    [Fact]
    public async Task Delayed_is_false_when_ScheduledAtUtc_is_still_in_the_future()
    {
        var tenantId = Guid.NewGuid();
        var nowUtc = new DateTimeOffset(2026, 3, 15, 0, 0, 0, TimeSpan.Zero);

        await SeedCleaningAsync(tenantId, "Assigned", scheduledAtUtc: nowUtc.AddDays(1));

        var overview = await GetOverviewAsync(tenantId, nowUtc.AddDays(-30), nowUtc.AddDays(30), nowUtc);

        overview.Housekeeping.Delayed.Should().Be(0);
    }

    [Fact]
    public async Task Delayed_is_false_for_a_Completed_cleaning_even_with_a_past_ScheduledAtUtc()
    {
        var tenantId = Guid.NewGuid();
        var nowUtc = new DateTimeOffset(2026, 3, 15, 0, 0, 0, TimeSpan.Zero);

        await SeedCompletedCleaningAsync(tenantId, completedAtUtc: nowUtc.AddDays(-1), scheduledAtUtc: nowUtc.AddDays(-5));

        var overview = await GetOverviewAsync(tenantId, nowUtc.AddDays(-30), nowUtc.AddDays(30), nowUtc);

        overview.Housekeeping.Delayed.Should().Be(0);
    }

    [Fact]
    public async Task Delayed_is_false_for_a_Cancelled_cleaning_even_with_a_past_ScheduledAtUtc()
    {
        var tenantId = Guid.NewGuid();
        var nowUtc = new DateTimeOffset(2026, 3, 15, 0, 0, 0, TimeSpan.Zero);

        await SeedCancelledCleaningAsync(tenantId, cancelledAtUtc: nowUtc.AddDays(-1), scheduledAtUtc: nowUtc.AddDays(-5));

        var overview = await GetOverviewAsync(tenantId, nowUtc.AddDays(-30), nowUtc.AddDays(30), nowUtc);

        overview.Housekeeping.Delayed.Should().Be(0);
    }

    /// <summary>Operationally still delayed while Interrupted (mandate §19 — no silent exception).</summary>
    [Fact]
    public async Task Delayed_is_true_for_an_Interrupted_cleaning_with_a_past_ScheduledAtUtc()
    {
        var tenantId = Guid.NewGuid();
        var nowUtc = new DateTimeOffset(2026, 3, 15, 0, 0, 0, TimeSpan.Zero);

        await SeedCleaningAsync(tenantId, "Interrupted", scheduledAtUtc: nowUtc.AddDays(-1));

        var overview = await GetOverviewAsync(tenantId, nowUtc.AddDays(-30), nowUtc.AddDays(30), nowUtc);

        overview.Housekeeping.Delayed.Should().Be(1);
    }

    [Fact]
    public async Task WaitingHelp_and_WaitingMaterials_are_current_state_counts()
    {
        var tenantId = Guid.NewGuid();
        await SeedCleaningAsync(tenantId, "WaitingHelp", scheduledAtUtc: null);
        await SeedCleaningAsync(tenantId, "WaitingMaterials", scheduledAtUtc: null);

        var overview = await GetOverviewAsync(tenantId);

        overview.Housekeeping.WaitingHelp.Should().Be(1);
        overview.Housekeeping.WaitingMaterials.Should().Be(1);
    }

    // ---- Occurrences ---------------------------------------------------

    [Fact]
    public async Task Occurrence_exactly_at_From_is_included_exactly_at_To_is_excluded()
    {
        var tenantId = Guid.NewGuid();
        var from = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(2026, 3, 31, 0, 0, 0, TimeSpan.Zero);

        await SeedOccurrenceAsync(tenantId, "Damage", from);
        await SeedOccurrenceAsync(tenantId, "Damage", to);

        var overview = await GetOverviewAsync(tenantId, from, to, from);

        overview.Occurrences.TotalInPeriod.Should().Be(1);
    }

    [Fact]
    public async Task ByType_distributes_over_every_type_used_in_the_period()
    {
        var tenantId = Guid.NewGuid();
        var from = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(2026, 3, 31, 0, 0, 0, TimeSpan.Zero);
        var moment = from.AddDays(1);

        await SeedOccurrenceAsync(tenantId, "Damage", moment);
        await SeedOccurrenceAsync(tenantId, "Damage", moment);
        await SeedOccurrenceAsync(tenantId, "Theft", moment);

        var overview = await GetOverviewAsync(tenantId, from, to, from);

        overview.Occurrences.ByType.Should().BeEquivalentTo(
        [
            new { Type = "Damage", Count = 2 },
            new { Type = "Theft", Count = 1 },
        ]);
    }

    [Fact]
    public async Task Tenant_isolation_overview_for_tenant_A_never_reflects_tenant_Bs_rows()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var from = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(2026, 3, 31, 0, 0, 0, TimeSpan.Zero);

        await SeedReservationAsync(tenantA, "confirmed", checkInAt: from.AddDays(1), checkOutAt: from.AddDays(3));
        await SeedReservationAsync(tenantB, "confirmed", checkInAt: from.AddDays(1), checkOutAt: from.AddDays(3));
        await SeedReservationAsync(tenantB, "confirmed", checkInAt: from.AddDays(1), checkOutAt: from.AddDays(3));

        var overviewA = await GetOverviewAsync(tenantA, from, to, from);
        var overviewB = await GetOverviewAsync(tenantB, from, to, from);

        overviewA.Reservations.CheckInsInPeriod.Should().Be(1);
        overviewB.Reservations.CheckInsInPeriod.Should().Be(2);
    }

    // ---- Seeding ---------------------------------------------------------

    private Task SeedReservationAsync(Guid tenantId, string status, DateTimeOffset checkInAt, DateTimeOffset checkOutAt) =>
        AddAsync(tenantId, dbContext => dbContext.ReservationProjection.Add(new DashboardReservationProjectionEntry(
            tenantId, Guid.NewGuid(), Guid.NewGuid(), status, checkInAt, checkOutAt, checkInAt)));

    private Task SeedCancelledReservationAsync(
        Guid tenantId, DateTimeOffset checkInAt, DateTimeOffset checkOutAt, DateTimeOffset cancelledAtUtc) =>
        AddAsync(tenantId, dbContext =>
        {
            var entry = new DashboardReservationProjectionEntry(
                tenantId, Guid.NewGuid(), Guid.NewGuid(), "confirmed", checkInAt, checkOutAt, checkInAt);
            entry.Cancel(cancelledAtUtc);
            dbContext.ReservationProjection.Add(entry);
        });

    private Task SeedCleaningAsync(Guid tenantId, string status, DateTimeOffset? scheduledAtUtc) =>
        AddAsync(tenantId, dbContext => dbContext.CleaningProjection.Add(new DashboardCleaningProjectionEntry(
            tenantId, Guid.NewGuid(), Guid.NewGuid(), status, scheduledAtUtc, DateTimeOffset.UtcNow)));

    private Task SeedCompletedCleaningAsync(Guid tenantId, DateTimeOffset completedAtUtc, DateTimeOffset? scheduledAtUtc = null) =>
        AddAsync(tenantId, dbContext =>
        {
            var entry = new DashboardCleaningProjectionEntry(
                tenantId, Guid.NewGuid(), Guid.NewGuid(), "Started", scheduledAtUtc, DateTimeOffset.UtcNow);
            entry.SetCompleted(completedAtUtc);
            entry.SetStatus("Completed", completedAtUtc);
            dbContext.CleaningProjection.Add(entry);
        });

    private Task SeedCancelledCleaningAsync(Guid tenantId, DateTimeOffset cancelledAtUtc, DateTimeOffset? scheduledAtUtc = null) =>
        AddAsync(tenantId, dbContext =>
        {
            var entry = new DashboardCleaningProjectionEntry(
                tenantId, Guid.NewGuid(), Guid.NewGuid(), "Pending", scheduledAtUtc, DateTimeOffset.UtcNow);
            entry.SetCancelled(cancelledAtUtc);
            entry.SetStatus("Cancelled", cancelledAtUtc);
            dbContext.CleaningProjection.Add(entry);
        });

    private Task SeedOccurrenceAsync(Guid tenantId, string type, DateTimeOffset registeredAtUtc) =>
        AddAsync(tenantId, dbContext => dbContext.OccurrenceProjection.Add(new DashboardOccurrenceProjectionEntry(
            tenantId, Guid.NewGuid(), Guid.NewGuid(), type, registeredAtUtc)));

    private async Task AddAsync(Guid tenantId, Action<DashboardDbContext> addEntity)
    {
        await using var dbContext = CreateDashboardDbContext(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        addEntity(dbContext);
        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();
    }

    // ---- Reading -----------------------------------------------------

    private async Task<Application.Overview.DashboardOverviewResult> GetOverviewAsync(
        Guid tenantId, DateTimeOffset? from = null, DateTimeOffset? to = null, DateTimeOffset? nowUtc = null)
    {
        var effectiveFrom = from ?? DateTimeOffset.UtcNow.AddDays(-30);
        var effectiveTo = to ?? DateTimeOffset.UtcNow.AddDays(30);
        var effectiveNow = nowUtc ?? DateTimeOffset.UtcNow;

        await using var dbContext = CreateDashboardDbContext(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        var reader = new DashboardOverviewReader(dbContext);
        return await reader.GetOverviewAsync(effectiveFrom, effectiveTo, effectiveNow, CancellationToken.None);
    }

    private static async Task SetTenantAsync(DbContext dbContext, Guid tenantId) =>
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.tenant_id', {tenantId.ToString()}, true)");

    private DashboardDbContext CreateDashboardDbContext(Guid tenantId)
    {
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);

        var options = new DbContextOptionsBuilder<DashboardDbContext>()
            .UseNpgsql(_fixture.MigratorConnectionString, npgsqlOptions =>
                npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "dashboard"))
            .Options;

        return new DashboardDbContext(options, tenantContext);
    }

    public sealed class Fixture : IAsyncLifetime
    {
        private const string MigratorRolePassword = "test_migrator_password";
        private const string AppRolePassword = "test_app_password";

        private PostgreSqlContainer _container = null!;
        public string MigratorConnectionString { get; private set; } = null!;

        public async Task InitializeAsync()
        {
            _container = new PostgreSqlBuilder()
                .WithImage("postgres:16")
                .WithDatabase("ihostpro_test")
                .WithUsername("ihostpro")
                .WithPassword("ihostpro_dev")
                .Build();

            await _container.StartAsync();

            var adminConnectionString = _container.GetConnectionString();

            await using (var adminConnection = new NpgsqlConnection(adminConnectionString))
            {
                await adminConnection.OpenAsync();
                await using var command = adminConnection.CreateCommand();
                command.CommandText = $"""
                    CREATE ROLE ihostpro_migrator LOGIN PASSWORD '{MigratorRolePassword}';
                    CREATE ROLE ihostpro_app LOGIN PASSWORD '{AppRolePassword}';
                    GRANT CREATE ON DATABASE ihostpro_test TO ihostpro_migrator;
                    """;
                await command.ExecuteNonQueryAsync();
            }

            var builder = new NpgsqlConnectionStringBuilder(adminConnectionString)
            {
                Username = "ihostpro_migrator",
                Password = MigratorRolePassword,
            };
            MigratorConnectionString = builder.ConnectionString;

            // Only Dashboard's own schema is needed — the reader queries
            // exclusively DashboardDbContext's own local projections, never
            // another context's schema.
            await using var dashboardDbContext = new DashboardDbContext(
                new DbContextOptionsBuilder<DashboardDbContext>()
                    .UseNpgsql(MigratorConnectionString, npgsqlOptions =>
                        npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "dashboard"))
                    .Options,
                new TenantContext());

            await dashboardDbContext.Database.MigrateAsync();
        }

        public async Task DisposeAsync() => await _container.DisposeAsync();
    }
}
