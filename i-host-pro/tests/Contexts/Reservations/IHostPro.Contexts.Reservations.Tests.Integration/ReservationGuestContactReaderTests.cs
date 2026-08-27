using FluentAssertions;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.Contexts.Reservations.Contracts;
using IHostPro.Contexts.Reservations.Domain;
using IHostPro.Contexts.Reservations.Infrastructure;
using IHostPro.Contexts.Reservations.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Testcontainers.PostgreSql;

namespace IHostPro.Contexts.Reservations.Tests.Integration;

/// <summary>
/// Exercises <see cref="IReservationGuestContactReader"/> — ADR-019's
/// purpose-limited Communication → Reservations synchronous read exception
/// (Fase 9, Checkpoint 1) — against a real PostgreSQL instance
/// (Testcontainers). Mirrors <c>PolicyResolutionTests</c>'s own composition-
/// root/seeding structure: <c>AddReservationsModule</c> needs only the
/// <c>reservations</c> schema for this reader, never Property Management.
/// </summary>
public class ReservationGuestContactReaderTests : IClassFixture<ReservationGuestContactReaderTests.Fixture>
{
    private readonly Fixture _fixture;

    public ReservationGuestContactReaderTests(Fixture fixture) => _fixture = fixture;

    public sealed class Fixture : IAsyncLifetime
    {
        private PostgreSqlContainer _container = null!;
        private ServiceProvider _serviceProvider = null!;
        public string ConnectionString { get; private set; } = null!;
        public CapturingLoggerProvider CapturingLogger { get; } = new();

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
            services.AddLogging(builder => builder.AddProvider(CapturingLogger));
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

    [Fact]
    public async Task GetGuestContactAsync_returns_null_when_the_reservation_does_not_exist()
    {
        var result = await ResolveAsync(Guid.NewGuid(), Guid.NewGuid());

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetGuestContactAsync_returns_the_phone_for_the_correct_tenant()
    {
        var tenantId = Guid.NewGuid();
        var reservationId = Guid.NewGuid();
        await SeedReservationAsync(tenantId, reservationId, "+5511999998888");

        var result = await ResolveAsync(tenantId, reservationId);

        result.Should().NotBeNull();
        result!.ReservationId.Should().Be(reservationId);
        result.GuestPhone.Should().Be("+5511999998888");
        result.GuestName.Should().Be("Test Guest", "GuestName was added in Fase 10, Checkpoint 4 (ADR-019 factual extension)");
    }

    [Fact]
    public async Task GetGuestContactAsync_returns_null_for_a_reservation_belonging_to_another_tenant()
    {
        var ownerTenantId = Guid.NewGuid();
        var otherTenantId = Guid.NewGuid();
        var reservationId = Guid.NewGuid();
        await SeedReservationAsync(ownerTenantId, reservationId, "+5511999998888");

        var result = await ResolveAsync(otherTenantId, reservationId);

        result.Should().BeNull("a cross-tenant reservationId must be indistinguishable from a non-existent one (ADR-019/RLS)");
    }

    [Fact]
    public async Task GetGuestContactAsync_returns_a_null_phone_when_the_reservation_has_none_on_file()
    {
        var tenantId = Guid.NewGuid();
        var reservationId = Guid.NewGuid();
        await SeedReservationAsync(tenantId, reservationId, guestPhone: null);

        var result = await ResolveAsync(tenantId, reservationId);

        result.Should().NotBeNull("a resolved Reservation with no phone on file is distinct from Reservation-not-found");
        result!.GuestPhone.Should().BeNull();
    }

    // ---- Audit ----

    [Fact]
    public async Task GetGuestContactAsync_emits_a_PII_safe_structured_audit_entry_and_never_logs_the_phone()
    {
        var tenantId = Guid.NewGuid();
        var reservationId = Guid.NewGuid();
        const string phone = "+5511977776666";
        await SeedReservationAsync(tenantId, reservationId, phone);

        await ResolveAsync(tenantId, reservationId);

        var entries = _fixture.CapturingLogger.Entries;
        entries.Should().NotContain(e => e.Message.Contains(phone), "the guest phone must never appear in any log entry");

        var matching = entries.Where(e =>
                e.Message.Contains(tenantId.ToString()) && e.Message.Contains(reservationId.ToString()))
            .ToList();
        matching.Should().ContainSingle("exactly one structured audit entry is expected per call (ADR-019, item 11)");
        matching[0].Message.Should().Contain("communication_delivery").And.Contain("Communication").And.Contain("Found");
    }

    [Fact]
    public async Task GetGuestContactAsync_audits_a_not_found_result_without_ever_mentioning_a_phone()
    {
        var tenantId = Guid.NewGuid();
        var reservationId = Guid.NewGuid();

        await ResolveAsync(tenantId, reservationId);

        var matching = _fixture.CapturingLogger.Entries
            .Where(e => e.Message.Contains(tenantId.ToString()) && e.Message.Contains(reservationId.ToString()))
            .ToList();
        matching.Should().ContainSingle();
        matching[0].Message.Should().Contain("NotFound");
    }

    public sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public List<(string CategoryName, string Message)> Entries { get; } = [];

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, Entries);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(string categoryName, List<(string, string)> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                entries.Add((categoryName, formatter(state, exception)));
        }
    }

    // ---- Helpers ----

    /// <summary>
    /// Mirrors <c>PolicyResolutionTests.SetAmbientTenant</c>: the reader's
    /// own throwaway <see cref="TenantContext"/> only drives the RLS session
    /// variable — the DI-scoped one (<c>ReservationsDbContext</c>'s Global
    /// Query Filter) must be set independently, exactly as
    /// <c>TenantResolutionMiddleware</c> would in production.
    /// </summary>
    private async Task<ReservationGuestContact?> ResolveAsync(Guid tenantId, Guid reservationId)
    {
        await using var scope = _fixture.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(tenantId);
        var reader = scope.ServiceProvider.GetRequiredService<IReservationGuestContactReader>();
        return await reader.GetGuestContactAsync(tenantId, reservationId, CancellationToken.None);
    }

    private async Task SeedReservationAsync(Guid tenantId, Guid reservationId, string? guestPhone)
    {
        await using var dbContext = CreateDbContext(_fixture.ConnectionString);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        var reservation = Reservation.Create(
            reservationId, tenantId, Guid.NewGuid(), "Test Guest", guestPhone,
            DateTimeOffset.UtcNow.AddDays(1), DateTimeOffset.UtcNow.AddDays(3), guestCount: 2, DateTimeOffset.UtcNow);
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
