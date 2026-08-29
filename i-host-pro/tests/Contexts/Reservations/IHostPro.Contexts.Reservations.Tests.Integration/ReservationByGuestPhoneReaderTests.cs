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
/// Exercises <see cref="IReservationByGuestPhoneReader"/> — ADR-029's
/// purpose-limited Communication → Reservations synchronous read exception
/// #13 (Fase 11, Checkpoint 1) — against a real PostgreSQL instance
/// (Testcontainers). Mirrors <c>ReservationGuestContactReaderTests</c>'s own
/// composition-root/seeding structure exactly.
/// </summary>
public class ReservationByGuestPhoneReaderTests : IClassFixture<ReservationByGuestPhoneReaderTests.Fixture>
{
    private readonly Fixture _fixture;

    public ReservationByGuestPhoneReaderTests(Fixture fixture) => _fixture = fixture;

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

    // A. 1 Confirmed matching phone → 1 candidate.
    [Fact]
    public async Task One_confirmed_reservation_with_matching_phone_returns_one_candidate()
    {
        var tenantId = Guid.NewGuid();
        var reservationId = Guid.NewGuid();
        var propertyId = Guid.NewGuid();
        await SeedReservationAsync(tenantId, reservationId, propertyId, "+55 11 99999-8888", ReservationSeedStatus.Confirmed);

        var candidates = await FindAsync(tenantId, "5511999998888");

        candidates.Should().ContainSingle();
        candidates[0].ReservationId.Should().Be(reservationId);
        candidates[0].PropertyId.Should().Be(propertyId);
    }

    // B. 2 Confirmed same phone → 2 candidates.
    [Fact]
    public async Task Two_confirmed_reservations_with_the_same_phone_return_two_candidates()
    {
        var tenantId = Guid.NewGuid();
        var reservationId1 = Guid.NewGuid();
        var reservationId2 = Guid.NewGuid();
        await SeedReservationAsync(tenantId, reservationId1, Guid.NewGuid(), "+5511999998888", ReservationSeedStatus.Confirmed);
        await SeedReservationAsync(tenantId, reservationId2, Guid.NewGuid(), "+5511999998888", ReservationSeedStatus.Confirmed);

        var candidates = await FindAsync(tenantId, "5511999998888");

        candidates.Should().HaveCount(2);
        candidates.Select(c => c.ReservationId).Should().BeEquivalentTo([reservationId1, reservationId2]);
    }

    // C. Cancelled same phone → excluded.
    [Fact]
    public async Task Cancelled_reservation_with_matching_phone_is_excluded()
    {
        var tenantId = Guid.NewGuid();
        await SeedReservationAsync(tenantId, Guid.NewGuid(), Guid.NewGuid(), "+5511999998888", ReservationSeedStatus.Cancelled);

        var candidates = await FindAsync(tenantId, "5511999998888");

        candidates.Should().BeEmpty("Cancelled is never eligible (ADR-029) — no temporal fallback exists");
    }

    // D. Closed same phone → excluded.
    [Fact]
    public async Task Closed_reservation_with_matching_phone_is_excluded()
    {
        var tenantId = Guid.NewGuid();
        await SeedReservationAsync(tenantId, Guid.NewGuid(), Guid.NewGuid(), "+5511999998888", ReservationSeedStatus.Closed);

        var candidates = await FindAsync(tenantId, "5511999998888");

        candidates.Should().BeEmpty("Closed is never eligible (ADR-029) — Confirmed is the sole lifecycle filter");
    }

    // E. Confirmed other tenant → excluded.
    [Fact]
    public async Task Confirmed_reservation_belonging_to_another_tenant_is_excluded()
    {
        var ownerTenantId = Guid.NewGuid();
        var otherTenantId = Guid.NewGuid();
        await SeedReservationAsync(ownerTenantId, Guid.NewGuid(), Guid.NewGuid(), "+5511999998888", ReservationSeedStatus.Confirmed);

        var candidates = await FindAsync(otherTenantId, "5511999998888");

        candidates.Should().BeEmpty("a cross-tenant match must be indistinguishable from no match at all (RLS)");
    }

    // F. different phone → excluded.
    [Fact]
    public async Task A_reservation_with_a_different_phone_is_excluded()
    {
        var tenantId = Guid.NewGuid();
        await SeedReservationAsync(tenantId, Guid.NewGuid(), Guid.NewGuid(), "+5511999998888", ReservationSeedStatus.Confirmed);

        var candidates = await FindAsync(tenantId, "5511977776666");

        candidates.Should().BeEmpty();
    }

    // G. normalization convention respected (digits-only, formatting characters ignored).
    [Fact]
    public async Task Phone_normalization_ignores_formatting_characters_on_both_sides()
    {
        var tenantId = Guid.NewGuid();
        var reservationId = Guid.NewGuid();
        await SeedReservationAsync(tenantId, reservationId, Guid.NewGuid(), "+55 (11) 99999-8888", ReservationSeedStatus.Confirmed);

        var candidates = await FindAsync(tenantId, "5511999998888");

        candidates.Should().ContainSingle("digits-only comparison must match regardless of +/spaces/parentheses/dashes on the stored value");
        candidates[0].ReservationId.Should().Be(reservationId);
    }

    [Fact]
    public async Task No_reservation_at_all_for_the_phone_returns_an_empty_list()
    {
        var candidates = await FindAsync(Guid.NewGuid(), "5511999998888");

        candidates.Should().BeEmpty();
    }

    // ---- Helpers ----

    private async Task<IReadOnlyList<ReservationCandidate>> FindAsync(Guid tenantId, string guestPhoneNormalized)
    {
        await using var scope = _fixture.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(tenantId);
        var reader = scope.ServiceProvider.GetRequiredService<IReservationByGuestPhoneReader>();
        return await reader.FindEligibleByGuestPhoneAsync(tenantId, guestPhoneNormalized, CancellationToken.None);
    }

    private enum ReservationSeedStatus
    {
        Confirmed,
        Cancelled,
        Closed,
    }

    private async Task SeedReservationAsync(
        Guid tenantId, Guid reservationId, Guid propertyId, string guestPhone, ReservationSeedStatus status)
    {
        await using var dbContext = CreateDbContext(_fixture.ConnectionString);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        var now = DateTimeOffset.UtcNow;
        var reservation = Reservation.Create(
            reservationId, tenantId, propertyId, "Test Guest", guestPhone,
            now.AddDays(1), now.AddDays(3), guestCount: 2, now);

        switch (status)
        {
            case ReservationSeedStatus.Cancelled:
                reservation.Cancel(now);
                break;
            case ReservationSeedStatus.Closed:
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
