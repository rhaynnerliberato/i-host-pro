using FluentAssertions;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.Contexts.Communication.Application;
using IHostPro.Contexts.Communication.Domain;
using IHostPro.Contexts.Communication.Infrastructure;
using IHostPro.Contexts.Communication.Infrastructure.Persistence;
using IHostPro.Contexts.Configuration.Contracts;
using IHostPro.Contexts.Reservations.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;

namespace IHostPro.Contexts.Communication.Tests.Integration;

/// <summary>
/// Drives <see cref="ICommunicationMessageExecutionScope"/> — the real
/// ADR-016 boundary, resolved exactly as the Wolverine adapter
/// (<c>ReservationCreatedHandler</c>) would — against a real PostgreSQL
/// instance (Fase 9, Checkpoint 1). Covers RLS/persistence/state-transition/
/// redelivery/connector success-failure. Never needs a Wolverine host at
/// all: <see cref="CommunicationDbContext"/> has no outbox (Communication
/// publishes no Integration Event this checkpoint), and
/// <c>CommunicationMessageExecutionScope</c> depends only on
/// <see cref="IServiceScopeFactory"/>, which any plain <see cref="ServiceProvider"/>
/// already supplies. Uses fake <see cref="ITemplateReader"/>/
/// <see cref="IReservationGuestContactReader"/> — the real cross-context
/// round trip is the separate real transport gate (task #415).
/// </summary>
public class CommunicationMessageExecutionScopeTests : IClassFixture<CommunicationMessageExecutionScopeTests.Fixture>
{
    private const string ActiveTemplateContent = "Check-in em {{CheckInDate}}";

    private readonly Fixture _fixture;

    public CommunicationMessageExecutionScopeTests(Fixture fixture) => _fixture = fixture;

    public sealed class Fixture : IAsyncLifetime
    {
        private const string AppRolePassword = "test_app_password";
        private const string MigratorRolePassword = "test_migrator_password";

        private PostgreSqlContainer _container = null!;
        public string MigratorConnectionString { get; private set; } = null!;
        public string AppConnectionString { get; private set; } = null!;

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

            var builder = new NpgsqlConnectionStringBuilder(adminConnectionString) { Username = "ihostpro_migrator", Password = MigratorRolePassword };
            MigratorConnectionString = builder.ConnectionString;
            builder.Username = "ihostpro_app";
            builder.Password = AppRolePassword;
            AppConnectionString = builder.ConnectionString;

            await using var dbContext = CreateDbContext(MigratorConnectionString);
            await dbContext.Database.MigrateAsync();
        }

        public async Task DisposeAsync() => await _container.DisposeAsync();

        private static CommunicationDbContext CreateDbContext(string connectionString)
        {
            var options = new DbContextOptionsBuilder<CommunicationDbContext>()
                .UseNpgsql(connectionString, npgsqlOptions => npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "communication"))
                .Options;
            return new CommunicationDbContext(options, new TenantContext());
        }
    }

    // ---- Composition root -------------------------------------------------

    private ServiceProvider BuildServiceProvider(
        ActiveTemplate? template, ReservationGuestContact? guestContact, IOutboundMessageConnector? connectorOverride = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Communication"] = _fixture.AppConnectionString })
            .Build();

        var services = new ServiceCollection();
        services.AddScoped<ITenantContext, TenantContext>();
        services.AddLogging();
        services.AddCommunicationModule(configuration);
        services.AddCommunicationReservationConsumer();
        services.AddScoped<ITemplateReader>(_ => FakeTemplateReader.Returning(template));
        services.AddScoped<IReservationGuestContactReader>(_ => FakeReservationGuestContactReader.Returning(guestContact));

        if (connectorOverride is not null)
            services.AddScoped(_ => connectorOverride);

        return services.BuildServiceProvider();
    }

    private static ReservationCreated NewEvent(Guid tenantId, Guid reservationId) => new()
    {
        TenantId = tenantId,
        AggregateId = reservationId,
        AggregateType = "Reservation",
        CorrelationId = Guid.NewGuid(),
        ActorType = "User",
        ActorId = Guid.NewGuid().ToString(),
        ReservationId = reservationId,
        PropertyId = Guid.NewGuid(),
        Status = "confirmed",
        CheckInAt = new DateTimeOffset(2026, 8, 20, 15, 0, 0, TimeSpan.Zero),
        Source = "manual",
    };

    private static async Task ExecuteAsync(ServiceProvider serviceProvider, ReservationCreated @event)
    {
        await using var scope = serviceProvider.CreateAsyncScope();
        var executionScope = scope.ServiceProvider.GetRequiredService<ICommunicationMessageExecutionScope>();
        await executionScope.ExecuteAsync(@event, @event.TenantId, Guid.NewGuid(), CancellationToken.None);
    }

    // ---- Persistence / state transition -----------------------------------

    [Fact]
    public async Task ExecuteAsync_persists_a_Message_and_marks_it_Sent_on_a_successful_dispatch()
    {
        var tenantId = Guid.NewGuid();
        var reservationId = Guid.NewGuid();
        using var serviceProvider = BuildServiceProvider(
            new ActiveTemplate("RESERVATION_CONFIRMATION", ActiveTemplateContent),
            new ReservationGuestContact(reservationId, "+5511999998888"));

        await ExecuteAsync(serviceProvider, NewEvent(tenantId, reservationId));

        var message = await ReadMessageAsync(tenantId, reservationId);
        message.Should().NotBeNull();
        message!.Status.Should().Be(MessageStatus.Sent);
        message.Channel.Should().Be("WhatsApp");
        message.TemplateKey.Should().Be("RESERVATION_CONFIRMATION");
    }

    [Fact]
    public async Task ExecuteAsync_marks_Failed_when_the_connector_rejects()
    {
        var tenantId = Guid.NewGuid();
        var reservationId = Guid.NewGuid();
        var rejectingConnector = FakeOutboundMessageConnector.Rejecting("provider_unavailable");
        using var serviceProvider = BuildServiceProvider(
            new ActiveTemplate("RESERVATION_CONFIRMATION", ActiveTemplateContent),
            new ReservationGuestContact(reservationId, "+5511999998888"),
            rejectingConnector);

        await ExecuteAsync(serviceProvider, NewEvent(tenantId, reservationId));

        var message = await ReadMessageAsync(tenantId, reservationId);
        message!.Status.Should().Be(MessageStatus.Failed);
        message.FailureReason.Should().Be("provider_unavailable");
        rejectingConnector.ReceivedDispatches.Should().ContainSingle();
    }

    [Fact]
    public async Task ExecuteAsync_marks_Failed_with_no_contact_available_and_never_calls_the_connector()
    {
        var tenantId = Guid.NewGuid();
        var reservationId = Guid.NewGuid();
        var connector = FakeOutboundMessageConnector.Succeeding();
        using var serviceProvider = BuildServiceProvider(
            new ActiveTemplate("RESERVATION_CONFIRMATION", ActiveTemplateContent),
            guestContact: null,
            connector);

        await ExecuteAsync(serviceProvider, NewEvent(tenantId, reservationId));

        var message = await ReadMessageAsync(tenantId, reservationId);
        message!.Status.Should().Be(MessageStatus.Failed);
        message.FailureReason.Should().Be("no_contact_available");
        connector.ReceivedDispatches.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_persists_the_providerMessageId_the_connector_reports_on_success()
    {
        var tenantId = Guid.NewGuid();
        var reservationId = Guid.NewGuid();
        var connector = FakeOutboundMessageConnector.SucceedingWithProviderMessageId("wamid.HBgL...");
        using var serviceProvider = BuildServiceProvider(
            new ActiveTemplate("RESERVATION_CONFIRMATION", ActiveTemplateContent),
            new ReservationGuestContact(reservationId, "+5511999998888"),
            connector);

        await ExecuteAsync(serviceProvider, NewEvent(tenantId, reservationId));

        var message = await ReadMessageAsync(tenantId, reservationId);
        message!.Status.Should().Be(MessageStatus.Sent);
        message.ProviderMessageId.Should().Be("wamid.HBgL...");
    }

    // ---- Redelivery idempotency --------------------------------------------

    [Fact]
    public async Task ExecuteAsync_redelivered_twice_creates_only_one_Message()
    {
        var tenantId = Guid.NewGuid();
        var reservationId = Guid.NewGuid();
        var connector = FakeOutboundMessageConnector.Succeeding();
        using var serviceProvider = BuildServiceProvider(
            new ActiveTemplate("RESERVATION_CONFIRMATION", ActiveTemplateContent),
            new ReservationGuestContact(reservationId, "+5511999998888"),
            connector);
        var @event = NewEvent(tenantId, reservationId);

        await ExecuteAsync(serviceProvider, @event);
        await ExecuteAsync(serviceProvider, @event);

        var count = await CountMessagesAsync(tenantId, reservationId);
        count.Should().Be(1, "a redelivered ReservationCreated must never create a second Message (CP1 idempotency)");
        connector.ReceivedDispatches.Should().ContainSingle("the second delivery must skip before ever reaching the connector");
    }

    // ---- RLS / tenant isolation ---------------------------------------------

    [Fact]
    public async Task A_Message_created_for_one_tenant_is_invisible_when_queried_as_another_tenant_RLS()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var reservationId = Guid.NewGuid();
        using var serviceProvider = BuildServiceProvider(
            new ActiveTemplate("RESERVATION_CONFIRMATION", ActiveTemplateContent),
            new ReservationGuestContact(reservationId, "+5511999998888"));

        await ExecuteAsync(serviceProvider, NewEvent(tenantA, reservationId));

        var visibleToOwner = await ReadMessageAsync(tenantA, reservationId);
        var visibleToOther = await ReadMessageAsync(tenantB, reservationId);

        visibleToOwner.Should().NotBeNull();
        visibleToOther.Should().BeNull("Row-Level Security must isolate messages per tenant, even for a known reservationId");
    }

    // ---- Helpers ------------------------------------------------------------

    private async Task<Message?> ReadMessageAsync(Guid tenantId, Guid reservationId)
    {
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);
        await using var dbContext = CreateDbContext(tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        var message = await dbContext.Messages.AsNoTracking()
            .FirstOrDefaultAsync(m => m.TenantId == tenantId && m.ReservationId == reservationId);

        await transaction.CommitAsync();
        return message;
    }

    private async Task<int> CountMessagesAsync(Guid tenantId, Guid reservationId)
    {
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);
        await using var dbContext = CreateDbContext(tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        var count = await dbContext.Messages.CountAsync(m => m.TenantId == tenantId && m.ReservationId == reservationId);

        await transaction.CommitAsync();
        return count;
    }

    private static async Task SetTenantAsync(CommunicationDbContext dbContext, Guid tenantId) =>
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.tenant_id', {tenantId.ToString()}, true)");

    private CommunicationDbContext CreateDbContext(ITenantContext tenantContext)
    {
        var options = new DbContextOptionsBuilder<CommunicationDbContext>()
            .UseNpgsql(_fixture.MigratorConnectionString, npgsqlOptions => npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "communication"))
            .Options;
        return new CommunicationDbContext(options, tenantContext);
    }
}
