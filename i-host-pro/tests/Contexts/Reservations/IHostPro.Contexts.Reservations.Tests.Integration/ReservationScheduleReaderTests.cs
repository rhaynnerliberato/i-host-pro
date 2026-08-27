using FluentAssertions;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.Contexts.Reservations.Contracts;
using IHostPro.Contexts.Reservations.Domain;
using IHostPro.Contexts.Reservations.Infrastructure;
using IHostPro.Contexts.Reservations.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;

namespace IHostPro.Contexts.Reservations.Tests.Integration;

/// <summary>
/// Exercises <see cref="IReservationScheduleReader"/> — ADR-024 amendment's
/// synchronous exception #7, Guest Operations → Reservations schedule
/// eligibility read (Fase 10, Checkpoint 3) — against a real PostgreSQL
/// instance (Testcontainers). Mirrors <c>ReservationGuestContactReaderTests</c>'s
/// own composition-root/seeding structure exactly.
/// </summary>
public class ReservationScheduleReaderTests : IClassFixture<ReservationScheduleReaderTests.Fixture>
{
    private readonly Fixture _fixture;

    public ReservationScheduleReaderTests(Fixture fixture) => _fixture = fixture;

    public sealed class Fixture : IAsyncLifetime
    {
        private PostgreSqlContainer _container = null!;
        private ServiceProvider _serviceProvider = null!;
        public string ConnectionString { get; private set; } = null!;

        public async Task InitializeAsync()
        {
            _container = new PostgreSqlBuilder()
                .WithImage("postgres:16")
                .WithDatabase("ihostpro_test")
                .WithUsername("ihostpro")
                .WithPassword("ihostpro_dev")
                .Build();

            await _container.StartAsync();
            ConnectionString = _container.GetConnectionString();

            await using (var adminConnection = new NpgsqlConnection(ConnectionString))
            {
                await adminConnection.OpenAsync();
                await using var command = adminConnection.CreateCommand();
                command.CommandText = """
                    CREATE ROLE ihostpro_app LOGIN PASSWORD 'test_app_password';
                    CREATE ROLE ihostpro_migrator LOGIN PASSWORD 'test_migrator_password';
                    """;
                await command.ExecuteNonQueryAsync();
            }

            var services = new ServiceCollection();
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Reservations"] = ConnectionString,
                })
                .Build();

            services.AddScoped<ITenantContext, TenantContext>();
            services.AddLogging();
            services.AddReservationsModule(configuration);
            _serviceProvider = services.BuildServiceProvider();

            await using var scope = _serviceProvider.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<ReservationsDbContext>();
            await dbContext.Database.MigrateAsync();
        }

        public async Task DisposeAsync()
        {
            await _serviceProvider.DisposeAsync();
            await _container.DisposeAsync();
        }

        public AsyncServiceScope CreateScope() => _serviceProvider.CreateAsyncScope();
    }

    // ---- GetScheduleAsync ----

    [Fact]
    public async Task GetScheduleAsync_returns_null_when_the_reservation_does_not_exist()
    {
        var result = await GetScheduleAsync(Guid.NewGuid(), Guid.NewGuid());

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetScheduleAsync_returns_null_for_a_reservation_belonging_to_another_tenant()
    {
        var ownerTenantId = Guid.NewGuid();
        var otherTenantId = Guid.NewGuid();
        var reservationId = Guid.NewGuid();
        await SeedReservationAsync(ownerTenantId, reservationId, Guid.NewGuid(), DateTimeOffset.UtcNow.AddDays(1), DateTimeOffset.UtcNow.AddDays(3));

        var result = await GetScheduleAsync(otherTenantId, reservationId);

        result.Should().BeNull("a cross-tenant reservationId must be indistinguishable from a non-existent one (RLS)");
    }

    [Fact]
    public async Task GetScheduleAsync_returns_the_confirmed_status_and_schedule()
    {
        var tenantId = Guid.NewGuid();
        var reservationId = Guid.NewGuid();
        var checkInAt = DateTimeOffset.UtcNow.AddDays(1);
        var checkOutAt = DateTimeOffset.UtcNow.AddDays(3);
        await SeedReservationAsync(tenantId, reservationId, Guid.NewGuid(), checkInAt, checkOutAt);

        var result = await GetScheduleAsync(tenantId, reservationId);

        result.Should().NotBeNull();
        result!.Status.Should().Be("confirmed");
        result.CheckInAt.Should().BeCloseTo(checkInAt, TimeSpan.FromSeconds(1));
        result.CheckOutAt.Should().BeCloseTo(checkOutAt, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task GetScheduleAsync_returns_the_cancelled_status()
    {
        var tenantId = Guid.NewGuid();
        var reservationId = Guid.NewGuid();
        await SeedReservationAsync(
            tenantId, reservationId, Guid.NewGuid(), DateTimeOffset.UtcNow.AddDays(1), DateTimeOffset.UtcNow.AddDays(3),
            transition: ReservationTransition.Cancel);

        var result = await GetScheduleAsync(tenantId, reservationId);

        result.Should().NotBeNull();
        result!.Status.Should().Be("cancelled");
    }

    [Fact]
    public async Task GetScheduleAsync_returns_the_closed_status()
    {
        var tenantId = Guid.NewGuid();
        var reservationId = Guid.NewGuid();
        await SeedReservationAsync(
            tenantId, reservationId, Guid.NewGuid(), DateTimeOffset.UtcNow.AddDays(1), DateTimeOffset.UtcNow.AddDays(3),
            transition: ReservationTransition.Close);

        var result = await GetScheduleAsync(tenantId, reservationId);

        result.Should().NotBeNull();
        result!.Status.Should().Be("closed");
    }

    // ---- HasConflictingReservationAsync ----

    [Fact]
    public async Task HasConflictingReservationAsync_returns_false_when_the_reservation_does_not_exist()
    {
        var result = await HasConflictAsync(
            Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1));

        result.Should().BeFalse();
    }

    [Fact]
    public async Task HasConflictingReservationAsync_excludes_the_reservation_itself_from_its_own_conflict_check()
    {
        var tenantId = Guid.NewGuid();
        var propertyId = Guid.NewGuid();
        var reservationId = Guid.NewGuid();
        var checkInAt = DateTimeOffset.UtcNow.AddDays(1);
        var checkOutAt = DateTimeOffset.UtcNow.AddDays(3);
        await SeedReservationAsync(tenantId, reservationId, propertyId, checkInAt, checkOutAt);

        // Requesting an even earlier check-in on the SAME reservation's own
        // window must never conflict against itself.
        var result = await HasConflictAsync(tenantId, reservationId, checkInAt.AddHours(-2), checkOutAt);

        result.Should().BeFalse("a reservation must never be reported as conflicting with itself (self-exclusion)");
    }

    [Fact]
    public async Task HasConflictingReservationAsync_returns_true_when_another_confirmed_reservation_overlaps_the_same_property()
    {
        var tenantId = Guid.NewGuid();
        var propertyId = Guid.NewGuid();
        var reservationId = Guid.NewGuid();
        await SeedReservationAsync(tenantId, reservationId, propertyId, DateTimeOffset.UtcNow.AddDays(5), DateTimeOffset.UtcNow.AddDays(7));

        // A second, Confirmed reservation on the SAME property, overlapping
        // the requested early-check-in window.
        var otherReservationId = Guid.NewGuid();
        await SeedReservationAsync(tenantId, otherReservationId, propertyId, DateTimeOffset.UtcNow.AddDays(1), DateTimeOffset.UtcNow.AddDays(3));

        var result = await HasConflictAsync(
            tenantId, reservationId, DateTimeOffset.UtcNow.AddDays(2), DateTimeOffset.UtcNow.AddDays(7));

        result.Should().BeTrue();
    }

    [Fact]
    public async Task HasConflictingReservationAsync_returns_false_when_the_overlapping_reservation_belongs_to_a_different_property()
    {
        var tenantId = Guid.NewGuid();
        var propertyId = Guid.NewGuid();
        var otherPropertyId = Guid.NewGuid();
        var reservationId = Guid.NewGuid();
        await SeedReservationAsync(tenantId, reservationId, propertyId, DateTimeOffset.UtcNow.AddDays(5), DateTimeOffset.UtcNow.AddDays(7));

        var otherReservationId = Guid.NewGuid();
        await SeedReservationAsync(tenantId, otherReservationId, otherPropertyId, DateTimeOffset.UtcNow.AddDays(1), DateTimeOffset.UtcNow.AddDays(3));

        var result = await HasConflictAsync(
            tenantId, reservationId, DateTimeOffset.UtcNow.AddDays(2), DateTimeOffset.UtcNow.AddDays(7));

        result.Should().BeFalse("a different property's reservation must never count as a conflict");
    }

    [Fact]
    public async Task HasConflictingReservationAsync_returns_false_when_the_overlapping_reservation_is_cancelled()
    {
        var tenantId = Guid.NewGuid();
        var propertyId = Guid.NewGuid();
        var reservationId = Guid.NewGuid();
        await SeedReservationAsync(tenantId, reservationId, propertyId, DateTimeOffset.UtcNow.AddDays(5), DateTimeOffset.UtcNow.AddDays(7));

        var otherReservationId = Guid.NewGuid();
        await SeedReservationAsync(
            tenantId, otherReservationId, propertyId, DateTimeOffset.UtcNow.AddDays(1), DateTimeOffset.UtcNow.AddDays(3),
            transition: ReservationTransition.Cancel);

        var result = await HasConflictAsync(
            tenantId, reservationId, DateTimeOffset.UtcNow.AddDays(2), DateTimeOffset.UtcNow.AddDays(7));

        result.Should().BeFalse("a Cancelled reservation must never count as a conflict");
    }

    [Fact]
    public async Task HasConflictingReservationAsync_returns_false_when_the_requested_window_does_not_overlap()
    {
        var tenantId = Guid.NewGuid();
        var propertyId = Guid.NewGuid();
        var reservationId = Guid.NewGuid();
        await SeedReservationAsync(tenantId, reservationId, propertyId, DateTimeOffset.UtcNow.AddDays(5), DateTimeOffset.UtcNow.AddDays(7));

        var otherReservationId = Guid.NewGuid();
        await SeedReservationAsync(tenantId, otherReservationId, propertyId, DateTimeOffset.UtcNow.AddDays(10), DateTimeOffset.UtcNow.AddDays(12));

        var result = await HasConflictAsync(
            tenantId, reservationId, DateTimeOffset.UtcNow.AddDays(4), DateTimeOffset.UtcNow.AddDays(7));

        result.Should().BeFalse();
    }

    // ---- Helpers ----

    private async Task<ReservationScheduleSnapshot?> GetScheduleAsync(Guid tenantId, Guid reservationId)
    {
        await using var scope = _fixture.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(tenantId);
        var reader = scope.ServiceProvider.GetRequiredService<IReservationScheduleReader>();
        return await reader.GetScheduleAsync(tenantId, reservationId, CancellationToken.None);
    }

    private async Task<bool> HasConflictAsync(
        Guid tenantId, Guid reservationId, DateTimeOffset requestedCheckInAt, DateTimeOffset requestedCheckOutAt)
    {
        await using var scope = _fixture.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(tenantId);
        var reader = scope.ServiceProvider.GetRequiredService<IReservationScheduleReader>();
        return await reader.HasConflictingReservationAsync(
            tenantId, reservationId, requestedCheckInAt, requestedCheckOutAt, CancellationToken.None);
    }

    private enum ReservationTransition
    {
        None,
        Cancel,
        Close,
    }

    private async Task SeedReservationAsync(
        Guid tenantId, Guid reservationId, Guid propertyId, DateTimeOffset checkInAt, DateTimeOffset checkOutAt,
        ReservationTransition transition = ReservationTransition.None)
    {
        await using var dbContext = CreateDbContext(_fixture.ConnectionString);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        var reservation = Reservation.Create(
            reservationId, tenantId, propertyId, "Test Guest", "+5511999998888",
            checkInAt, checkOutAt, guestCount: 2, DateTimeOffset.UtcNow);

        var now = DateTimeOffset.UtcNow;
        switch (transition)
        {
            case ReservationTransition.Cancel:
                reservation.Cancel(now);
                break;
            case ReservationTransition.Close:
                reservation.Close(now);
                break;
        }

        dbContext.Reservations.Add(reservation);

        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();
    }

    private static async Task SetTenantAsync(ReservationsDbContext dbContext, Guid tenantId) =>
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.tenant_id', {tenantId.ToString()}, true)");

    private static ReservationsDbContext CreateDbContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<ReservationsDbContext>()
            .UseNpgsql(connectionString, npgsqlOptions =>
                npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "reservations"))
            .Options;

        return new ReservationsDbContext(options, new TenantContext());
    }
}
