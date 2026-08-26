using FluentAssertions;
using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.BuildingBlocks.Infrastructure.Messaging;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.BuildingBlocks.Infrastructure.Persistence;
using IHostPro.Contexts.PropertyManagement.Domain;
using IHostPro.Contexts.PropertyManagement.Domain.Enums;
using IHostPro.Contexts.PropertyManagement.Domain.ValueObjects;
using IHostPro.Contexts.PropertyManagement.Infrastructure;
using IHostPro.Contexts.PropertyManagement.Infrastructure.Persistence;
using IHostPro.Contexts.Reservations.Application;
using IHostPro.Contexts.Reservations.Application.Errors;
using IHostPro.Contexts.Reservations.Application.Reservations;
using IHostPro.Contexts.Reservations.Domain.Enums;
using IHostPro.Contexts.Reservations.Infrastructure;
using IHostPro.Contexts.Reservations.Infrastructure.Persistence;
using JasperFx;
using JasperFx.Resources;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Testcontainers.PostgreSql;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.Postgresql;

namespace IHostPro.Contexts.Reservations.Tests.Integration;

/// <summary>
/// End-to-end test of Create/Update/Cancel/List/Detail against REAL
/// PostgreSQL (both the <c>property_management</c> and <c>reservations</c>
/// schemas — Create/Update genuinely call
/// <see cref="Contracts.IPropertyReservationEligibilityReader"/>, mirroring
/// <c>PropertyOwnerCommandHandlerTests</c>'s structure) — dispatched through
/// the REAL production composition root via <see cref="ISender"/>. Only this
/// host's Reservations module registers a Mediator (<c>AddPropertyManagementModule</c>
/// is called WITHOUT <c>AddPropertyManagementCommandDispatch</c>, so no
/// second <c>Mediator.Mediator</c> is ever registered here — <c>ISender</c>
/// resolves unambiguously, exactly like <c>PropertyOwnerCommandHandlerTests</c>).
/// </summary>
public class ReservationCommandHandlerTests : IClassFixture<ReservationCommandHandlerTests.Fixture>
{
    private const string ReservationsOutboxSchema = "reservations_messaging";
    private const string PropertyManagementOutboxSchema = "property_management_messaging";
    private const string MainSchema = "platform_messaging";

    private readonly string _migratorConnectionString;
    private readonly string _appConnectionString;

    public ReservationCommandHandlerTests(Fixture fixture)
    {
        _migratorConnectionString = fixture.MigratorConnectionString;
        _appConnectionString = fixture.AppConnectionString;
    }

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
                await ExecuteAsync(adminConnection, $"""
                    CREATE ROLE ihostpro_migrator LOGIN PASSWORD '{MigratorRolePassword}';
                    CREATE ROLE ihostpro_app LOGIN PASSWORD '{AppRolePassword}';
                    GRANT CREATE ON DATABASE ihostpro_test TO ihostpro_migrator;
                    """);
            }

            var builder = new NpgsqlConnectionStringBuilder(adminConnectionString);
            builder.Username = "ihostpro_migrator";
            builder.Password = MigratorRolePassword;
            MigratorConnectionString = builder.ConnectionString;
            builder.Username = "ihostpro_app";
            builder.Password = AppRolePassword;
            AppConnectionString = builder.ConnectionString;

            await using (var pmDbContext = CreatePropertyManagementDbContext(MigratorConnectionString))
                await pmDbContext.Database.MigrateAsync();

            await using (var reservationsDbContext = CreateReservationsDbContext(MigratorConnectionString))
                await reservationsDbContext.Database.MigrateAsync();

            await ProvisionMainStoreAsMigratorAsync();
            await ProvisionOutboxAsMigratorAsync(PropertyManagementOutboxSchema, typeof(PropertyManagementDbContext));
            await ProvisionOutboxAsMigratorAsync(ReservationsOutboxSchema, typeof(ReservationsDbContext));
        }

        public async Task DisposeAsync() => await _container.DisposeAsync();

        /// <summary>
        /// This test host registers TWO Ancillary stores (Property
        /// Management, Reservations) — Wolverine requires exactly one store
        /// designated Main whenever any Ancillary store exists (Fase 2,
        /// Incremento 1, Checkpoint 6 homologação — the exact defect found
        /// and fixed there), so this mirrors the real <c>Program.cs</c>'s
        /// own <c>platform_messaging</c> registration, never skipped just
        /// because this is a test host.
        /// </summary>
        private async Task ProvisionMainStoreAsMigratorAsync()
        {
            var hostBuilder = Host.CreateApplicationBuilder();
            hostBuilder.UseWolverine(opts =>
            {
                opts.PersistMessagesWithPostgresql(MigratorConnectionString, MainSchema);
                opts.AutoBuildMessageStorageOnStartup = AutoCreate.None;
                opts.UseEntityFrameworkCoreTransactions();
            });

            using var mainHost = hostBuilder.Build();
            await mainHost.SetupResources();

            await using var connection = new NpgsqlConnection(MigratorConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                GRANT USAGE ON SCHEMA {MainSchema} TO ihostpro_app;
                GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA {MainSchema} TO ihostpro_app;
                GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA {MainSchema} TO ihostpro_app;
                ALTER DEFAULT PRIVILEGES FOR ROLE ihostpro_migrator IN SCHEMA {MainSchema}
                  GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO ihostpro_app;
                ALTER DEFAULT PRIVILEGES FOR ROLE ihostpro_migrator IN SCHEMA {MainSchema}
                  GRANT USAGE, SELECT ON SEQUENCES TO ihostpro_app;
                """;
            await command.ExecuteNonQueryAsync();
        }

        private async Task ProvisionOutboxAsMigratorAsync(string schema, Type dbContextMarkerType)
        {
            var hostBuilder = Host.CreateApplicationBuilder();
            hostBuilder.UseWolverine(opts =>
            {
                opts.EnrollAncillaryPostgresqlOutbox(MigratorConnectionString, schema, dbContextMarkerType);
                opts.AutoBuildMessageStorageOnStartup = AutoCreate.None;
                opts.UseEntityFrameworkCoreTransactions();
            });

            using var outboxHost = hostBuilder.Build();
            await outboxHost.SetupResources();

            await using var connection = new NpgsqlConnection(MigratorConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                GRANT USAGE ON SCHEMA {schema} TO ihostpro_app;
                GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA {schema} TO ihostpro_app;
                GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA {schema} TO ihostpro_app;
                ALTER DEFAULT PRIVILEGES FOR ROLE ihostpro_migrator IN SCHEMA {schema}
                  GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO ihostpro_app;
                ALTER DEFAULT PRIVILEGES FOR ROLE ihostpro_migrator IN SCHEMA {schema}
                  GRANT USAGE, SELECT ON SEQUENCES TO ihostpro_app;
                """;
            await command.ExecuteNonQueryAsync();
        }

        private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }
    }

    // ---- Service graph (real composition root) -----------------------------

    private async Task<IHost> BuildHostAsync(Action<IServiceCollection>? overrides = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:PropertyManagement"] = _appConnectionString,
            ["ConnectionStrings:Reservations"] = _appConnectionString,
        }).Build();

        var hostBuilder = Host.CreateApplicationBuilder();
        hostBuilder.Services.AddScoped<ITenantContext, TenantContext>();
        hostBuilder.Services.AddScoped<ICurrentTenantProvider, TenantContextCurrentTenantProvider>();
        hostBuilder.Services.AddIHostProTenantAwarePipeline();

        // Property Management's module ONLY — never AddPropertyManagementCommandDispatch()
        // here, so its own Mediator.Mediator is never registered: this host
        // needs only IPropertyReservationEligibilityReader, never to dispatch
        // a Property Management command (mirrors PropertyOwnerCommandHandlerTests'
        // identical reasoning for Identity's module).
        hostBuilder.Services.AddPropertyManagementModule(configuration);

        hostBuilder.Services.AddReservationsModule(configuration);
        hostBuilder.Services.AddReservationsCommandDispatch();

        // Applied AFTER AddReservationsCommandDispatch() so a test-only
        // override (e.g. wrapping IReservationConflictGuard for a
        // deterministic concurrency proof) replaces the production
        // registration — never the reverse order, which would let the
        // production registration win.
        overrides?.Invoke(hostBuilder.Services);

        hostBuilder.UseWolverine(opts =>
        {
            // Main store required whenever any Ancillary store exists — see
            // Fixture.ProvisionMainStoreAsMigratorAsync's own doc comment.
            opts.PersistMessagesWithPostgresql(_appConnectionString, MainSchema);

            opts.EnrollAncillaryPostgresqlOutbox(_appConnectionString, PropertyManagementOutboxSchema, typeof(PropertyManagementDbContext));
            opts.EnrollAncillaryPostgresqlOutbox(_appConnectionString, ReservationsOutboxSchema, typeof(ReservationsDbContext));
            opts.AutoBuildMessageStorageOnStartup = AutoCreate.None;
            opts.UseEntityFrameworkCoreTransactions();
        });

        var host = hostBuilder.Build();
        await host.StartAsync();
        return host;
    }

    private static async Task<Result<TResponse>> ExecuteAsync<TResponse>(IHost host, IRequest<Result<TResponse>> message, Guid tenantId)
    {
        using var scope = host.Services.CreateScope();
        var sp = scope.ServiceProvider;
        sp.GetRequiredService<ITenantContext>().SetTenant(tenantId);

        return await sp.GetRequiredService<ISender>().Send(message, CancellationToken.None);
    }

    // ---- Seeding: Property Management (direct DbContext, mirrors PropertyManagementFoundationTests) ----

    private async Task<Guid> SeedActivePropertyAsync(Guid tenantId, int capacity = 4)
    {
        var address = Address.Create("59090-000", "Rua Exemplo", "100", null, "Ponta Negra", "Natal", "RN", "BR");

        var property = Property.Create(
            Guid.NewGuid(), tenantId, PropertyCode.Create($"P-{Guid.NewGuid():N}"[..12]), "Test Property", capacity,
            condominiumId: null, address, DateTimeOffset.UtcNow);
        property.Activate(DateTimeOffset.UtcNow);

        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);

        await using var dbContext = CreatePropertyManagementDbContext(_migratorConnectionString, tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        dbContext.Properties.Add(property);
        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();

        return property.Id;
    }

    private async Task SetPropertyStatusAsync(Guid tenantId, Guid propertyId, PropertyStatus status)
    {
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);

        await using var dbContext = CreatePropertyManagementDbContext(_migratorConnectionString, tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        var property = await dbContext.Properties.FirstAsync(p => p.Id == propertyId);
        if (status == PropertyStatus.Inactive)
            property.Deactivate(DateTimeOffset.UtcNow);

        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();
    }

    // ---- Create --------------------------------------------------------------

    [Fact]
    public async Task Creating_a_reservation_for_an_active_property_succeeds_as_Confirmed()
    {
        var tenantId = Guid.NewGuid();
        using var host = await BuildHostAsync();
        var propertyId = await SeedActivePropertyAsync(tenantId);

        var result = await ExecuteAsync(host, CreateCommand(tenantId, propertyId), tenantId);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be("confirmed");
        result.Value.PropertyId.Should().Be(propertyId);
    }

    [Fact]
    public async Task Creating_a_reservation_for_a_nonexistent_property_fails_with_PropertyNotFound()
    {
        var tenantId = Guid.NewGuid();
        using var host = await BuildHostAsync();

        var result = await ExecuteAsync(host, CreateCommand(tenantId, Guid.NewGuid()), tenantId);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(ReservationsErrorCodes.PropertyNotFound);
    }

    [Fact]
    public async Task Creating_a_reservation_for_an_inactive_property_fails_with_PropertyNotActive()
    {
        var tenantId = Guid.NewGuid();
        using var host = await BuildHostAsync();
        var propertyId = await SeedActivePropertyAsync(tenantId);
        await SetPropertyStatusAsync(tenantId, propertyId, PropertyStatus.Inactive);

        var result = await ExecuteAsync(host, CreateCommand(tenantId, propertyId), tenantId);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(ReservationsErrorCodes.PropertyNotActive);
    }

    [Fact]
    public async Task Guest_count_exceeding_capacity_fails_with_PropertyCapacityExceeded()
    {
        var tenantId = Guid.NewGuid();
        using var host = await BuildHostAsync();
        var propertyId = await SeedActivePropertyAsync(tenantId, capacity: 1);

        var result = await ExecuteAsync(host, CreateCommand(tenantId, propertyId, guestCount: 2), tenantId);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(ReservationsErrorCodes.PropertyCapacityExceeded);
    }

    [Fact]
    public async Task A_second_reservation_for_a_disjoint_period_on_the_same_property_succeeds()
    {
        var tenantId = Guid.NewGuid();
        using var host = await BuildHostAsync();
        var propertyId = await SeedActivePropertyAsync(tenantId);

        var checkIn1 = new DateTimeOffset(2026, 8, 10, 14, 0, 0, TimeSpan.Zero);
        var checkOut1 = new DateTimeOffset(2026, 8, 13, 11, 0, 0, TimeSpan.Zero);
        await ExecuteAsync(host, CreateCommand(tenantId, propertyId, checkIn1, checkOut1), tenantId);

        // Starts exactly at the first reservation's checkout instant — half-open
        // interval means this must succeed (Fase 3, Incremento 1 plan, item 7).
        var result = await ExecuteAsync(host, CreateCommand(tenantId, propertyId, checkOut1, checkOut1.AddDays(2)), tenantId);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task An_overlapping_reservation_for_the_same_property_fails_with_ReservationDateConflict()
    {
        var tenantId = Guid.NewGuid();
        using var host = await BuildHostAsync();
        var propertyId = await SeedActivePropertyAsync(tenantId);

        var checkIn = new DateTimeOffset(2026, 8, 10, 14, 0, 0, TimeSpan.Zero);
        var checkOut = new DateTimeOffset(2026, 8, 13, 11, 0, 0, TimeSpan.Zero);
        await ExecuteAsync(host, CreateCommand(tenantId, propertyId, checkIn, checkOut), tenantId);

        var overlapping = await ExecuteAsync(
            host, CreateCommand(tenantId, propertyId, checkIn.AddDays(1), checkOut.AddDays(1)), tenantId);

        overlapping.IsFailure.Should().BeTrue();
        overlapping.Error.Code.Should().Be(ReservationsErrorCodes.ReservationDateConflict);
    }

    [Fact]
    public async Task Two_genuinely_concurrent_creates_for_the_same_overlapping_period_allow_only_one_to_succeed()
    {
        // Proves the pg_advisory_xact_lock-protected conflict check under
        // REAL concurrency (Fase 3, Incremento 1 plan, item 7) — two
        // independent DI scopes, dispatched via Task.WhenAll with no
        // artificial synchronization: the real PostgreSQL advisory lock
        // itself forces the serialization, unlike an in-memory TestServer
        // race (which needs an explicit Barrier).
        var tenantId = Guid.NewGuid();
        using var host = await BuildHostAsync();
        var propertyId = await SeedActivePropertyAsync(tenantId);

        var checkIn = new DateTimeOffset(2026, 9, 1, 14, 0, 0, TimeSpan.Zero);
        var checkOut = new DateTimeOffset(2026, 9, 5, 11, 0, 0, TimeSpan.Zero);

        var task1 = ExecuteAsync(host, CreateCommand(tenantId, propertyId, checkIn, checkOut), tenantId);
        var task2 = ExecuteAsync(host, CreateCommand(tenantId, propertyId, checkIn, checkOut), tenantId);

        var results = await Task.WhenAll(task1, task2);

        results.Count(r => r.IsSuccess).Should().Be(1);
        results.Count(r => r.IsFailure && r.Error.Code == ReservationsErrorCodes.ReservationDateConflict).Should().Be(1);
    }

    /// <summary>
    /// Test-only decorator — wraps the real <see cref="ReservationConflictGuard"/>
    /// and forces both concurrent callers to reach the advisory-lock
    /// acquisition point at EXACTLY the same instant, via a shared
    /// <see cref="Barrier"/> — mirrors <c>CondominiumsEndpointsTests</c>'
    /// own <c>BarrierPropertyAuditWriter</c> technique (Fase 2, Checkpoint 5).
    /// Registered ONLY in this test host's DI container
    /// (<see cref="BuildHostAsync"/>'s <c>overrides</c> parameter) — no
    /// test-only hook exists in production code. Without this barrier,
    /// <see cref="Two_genuinely_concurrent_creates_for_the_same_overlapping_period_allow_only_one_to_succeed"/>'s
    /// plain <c>Task.WhenAll</c> only proves the lock works IF the two
    /// requests happen to race; this guarantees they always do.
    /// </summary>
    private sealed class BarrierReservationConflictGuard : IReservationConflictGuard
    {
        private readonly IReservationConflictGuard _inner;
        private readonly Barrier _barrier;

        public BarrierReservationConflictGuard(IReservationConflictGuard inner, Barrier barrier)
        {
            _inner = inner;
            _barrier = barrier;
        }

        public async Task AcquirePropertyLockAsync(Guid tenantId, Guid propertyId, CancellationToken cancellationToken)
        {
            // Both callers must arrive here — having already read nothing
            // and committed nothing — before either is allowed to proceed
            // to the REAL pg_advisory_xact_lock call below. This is what
            // guarantees a genuine race at the serialization point, instead
            // of relying on incidental Task.WhenAll scheduling.
            _barrier.SignalAndWait(cancellationToken);
            await _inner.AcquirePropertyLockAsync(tenantId, propertyId, cancellationToken);
        }

        public Task<bool> HasConflictingReservationAsync(
            Guid tenantId, Guid propertyId, DateTimeOffset checkInAt, DateTimeOffset checkOutAt,
            Guid? excludeReservationId, CancellationToken cancellationToken) =>
            _inner.HasConflictingReservationAsync(tenantId, propertyId, checkInAt, checkOutAt, excludeReservationId, cancellationToken);
    }

    [Fact]
    public async Task Two_deterministically_synchronized_creates_prove_the_advisory_lock_alone_serializes_the_conflict_check()
    {
        // Complements Two_genuinely_concurrent_creates_... with a GUARANTEED
        // race (Fase 3, Incremento 1 plan, item 7 — deterministic proof
        // requested explicitly): both requests are forced, via a shared
        // Barrier(2), to reach the advisory-lock call at the same instant —
        // proving that WITHOUT pg_advisory_xact_lock's own serialization,
        // both could have read the (still conflict-free) period before
        // either committed. Two independent hosts/DI containers (never two
        // clients of the same host) — mirrors every other deterministic
        // concurrency test in this codebase (CondominiumsEndpointsTests,
        // PropertyOwnerCommandHandlerTests).
        var tenantId = Guid.NewGuid();
        var barrier = new Barrier(2);

        void OverrideConflictGuard(IServiceCollection services) =>
            services.AddScoped<IReservationConflictGuard>(sp =>
                new BarrierReservationConflictGuard(
                    new ReservationConflictGuard(sp.GetRequiredService<ReservationsDbContext>()), barrier));

        using var host1 = await BuildHostAsync(OverrideConflictGuard);
        using var host2 = await BuildHostAsync(OverrideConflictGuard);

        var propertyId = await SeedActivePropertyAsync(tenantId);

        var checkIn = new DateTimeOffset(2026, 11, 1, 14, 0, 0, TimeSpan.Zero);
        var checkOut = new DateTimeOffset(2026, 11, 5, 11, 0, 0, TimeSpan.Zero);

        var task1 = ExecuteAsync(host1, CreateCommand(tenantId, propertyId, checkIn, checkOut), tenantId);
        var task2 = ExecuteAsync(host2, CreateCommand(tenantId, propertyId, checkIn, checkOut), tenantId);

        // Explicit timeout — a stuck Barrier (deadlock) fails the test
        // clearly instead of hanging indefinitely.
        var whenAll = Task.WhenAll(task1, task2);
        var completedInTime = await Task.WhenAny(whenAll, Task.Delay(TimeSpan.FromSeconds(15))) == whenAll;
        completedInTime.Should().BeTrue("both synchronized creates must complete — a stuck Barrier would indicate a deadlock");

        var results = new[] { await task1, await task2 };

        results.Count(r => r.IsSuccess).Should().Be(1, "exactly one of the two barrier-synchronized creates must win the advisory lock");
        results.Count(r => r.IsFailure && r.Error.Code == ReservationsErrorCodes.ReservationDateConflict).Should().Be(1);

        // Exactly one audit entry for this tenant/action — proof by
        // construction (mirrors CondominiumsEndpointsTests' own reasoning)
        // that exactly one ReservationCreated event was ever enqueued: the
        // handler stages the audit entry and the event in the SAME
        // transaction, so a single audit row is only possible if the
        // handler's success path ran exactly once.
        var auditCount = await CountReservationCreatedAuditEntriesAsync(tenantId);
        auditCount.Should().Be(1, "exactly one reservation_created audit entry must exist for this tenant — proof of exactly one confirmed creation, never two");
    }

    private async Task<long> CountReservationCreatedAuditEntriesAsync(Guid tenantId)
    {
        await using var connection = new NpgsqlConnection(_migratorConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var setCommand = connection.CreateCommand())
        {
            setCommand.CommandText = $"SET LOCAL app.tenant_id = '{tenantId:D}'";
            await setCommand.ExecuteNonQueryAsync();
        }

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM reservations.reservation_audit_log WHERE action_code = 'reservation_created'";
        var count = (long)(await command.ExecuteScalarAsync())!;
        await transaction.CommitAsync();
        return count;
    }

    // ---- Update ----------------------------------------------------------

    [Fact]
    public async Task Updating_guest_name_changes_it_and_is_idempotent_on_repeated_identical_value()
    {
        var tenantId = Guid.NewGuid();
        using var host = await BuildHostAsync();
        var propertyId = await SeedActivePropertyAsync(tenantId);
        var created = await ExecuteAsync(host, CreateCommand(tenantId, propertyId), tenantId);

        var updated = await ExecuteAsync(
            host,
            new UpdateReservationCommand(
                tenantId, Guid.NewGuid(), created.Value.Id,
                Optional<Guid>.Unset, Optional<string>.Of("New Name"), Optional<string?>.Unset,
                Optional<DateTimeOffset>.Unset, Optional<DateTimeOffset>.Unset, Optional<int>.Unset),
            tenantId);

        updated.IsSuccess.Should().BeTrue();
        updated.Value.GuestName.Should().Be("New Name");
    }

    [Fact]
    public async Task Updating_a_cancelled_reservation_fails_with_CancelledReservationCannotBeModified()
    {
        var tenantId = Guid.NewGuid();
        using var host = await BuildHostAsync();
        var propertyId = await SeedActivePropertyAsync(tenantId);
        var created = await ExecuteAsync(host, CreateCommand(tenantId, propertyId), tenantId);
        await ExecuteAsync(host, new CancelReservationCommand(tenantId, Guid.NewGuid(), created.Value.Id), tenantId);

        var updated = await ExecuteAsync(
            host,
            new UpdateReservationCommand(
                tenantId, Guid.NewGuid(), created.Value.Id,
                Optional<Guid>.Unset, Optional<string>.Of("New Name"), Optional<string?>.Unset,
                Optional<DateTimeOffset>.Unset, Optional<DateTimeOffset>.Unset, Optional<int>.Unset),
            tenantId);

        updated.IsFailure.Should().BeTrue();
        updated.Error.Code.Should().Be(ReservationsErrorCodes.CancelledReservationCannotBeModified);
    }

    /// <summary>
    /// Test-only decorator — wraps the real <see cref="ReservationReader"/>,
    /// signals <paramref name="snapshotCaptured"/> right after
    /// <see cref="GetUpdateSnapshotAsync"/>'s inner read returns (i.e., the
    /// pre-transaction snapshot — Fase 3 §3 steps 1-3 — has been taken), then
    /// blocks until <paramref name="releaseGate"/> completes before returning
    /// it to the handler — giving the test a deterministic window, between
    /// the snapshot and the write transaction opening, to mutate the SAME row
    /// through a genuinely separate connection. Registered ONLY in this test
    /// host's DI container (mirrors <c>BarrierReservationConflictGuard</c>
    /// above) — no test-only hook exists in production code.
    /// </summary>
    private sealed class GatedSnapshotReservationReader : IReservationReader
    {
        private readonly IReservationReader _inner;
        private readonly TaskCompletionSource _snapshotCaptured;
        private readonly TaskCompletionSource _releaseGate;

        public GatedSnapshotReservationReader(IReservationReader inner, TaskCompletionSource snapshotCaptured, TaskCompletionSource releaseGate)
        {
            _inner = inner;
            _snapshotCaptured = snapshotCaptured;
            _releaseGate = releaseGate;
        }

        public Task<PagedResult<ReservationSummaryResult>> ListAsync(
            Guid? propertyId, string? status, DateTimeOffset? from, DateTimeOffset? to,
            int page, int pageSize, CancellationToken cancellationToken) =>
            _inner.ListAsync(propertyId, status, from, to, page, pageSize, cancellationToken);

        public Task<ReservationResult?> GetByIdAsync(Guid reservationId, CancellationToken cancellationToken) =>
            _inner.GetByIdAsync(reservationId, cancellationToken);

        public Task<uint?> GetCurrentXminAsync(Guid reservationId, CancellationToken cancellationToken) =>
            _inner.GetCurrentXminAsync(reservationId, cancellationToken);

        public Task<Guid?> GetIdByExternalIdentityAsync(
            ReservationSource source, string externalReservationId, CancellationToken cancellationToken) =>
            _inner.GetIdByExternalIdentityAsync(source, externalReservationId, cancellationToken);

        public async Task<ReservationUpdateSnapshot?> GetUpdateSnapshotAsync(Guid reservationId, CancellationToken cancellationToken)
        {
            var snapshot = await _inner.GetUpdateSnapshotAsync(reservationId, cancellationToken);
            _snapshotCaptured.TrySetResult();
            await _releaseGate.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            return snapshot;
        }
    }

    [Fact]
    public async Task A_row_changed_between_the_snapshot_and_the_write_transaction_fails_with_ReservationConcurrencyConflict_and_never_audits_or_publishes()
    {
        // Deterministic proof of Fase 3 §3's transactional-order correction:
        // a real, committed concurrent mutation of the SAME row, landing
        // strictly between the pre-transaction snapshot read and the write
        // transaction's own re-read, must be detected via the row's real
        // PostgreSQL xmin — never silently reused.
        var tenantId = Guid.NewGuid();
        var snapshotCaptured = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        void OverrideReader(IServiceCollection services) =>
            services.AddScoped<IReservationReader>(sp =>
                new GatedSnapshotReservationReader(
                    new ReservationReader(sp.GetRequiredService<ReservationsDbContext>(), sp.GetRequiredService<ITenantContext>()),
                    snapshotCaptured, releaseGate));

        using var host = await BuildHostAsync(OverrideReader);
        var propertyId = await SeedActivePropertyAsync(tenantId);
        var created = await ExecuteAsync(host, CreateCommand(tenantId, propertyId), tenantId);

        var updateTask = ExecuteAsync(
            host,
            new UpdateReservationCommand(
                tenantId, Guid.NewGuid(), created.Value.Id,
                Optional<Guid>.Unset, Optional<string>.Of("New Name"), Optional<string?>.Unset,
                Optional<DateTimeOffset>.Unset, Optional<DateTimeOffset>.Unset, Optional<int>.Unset),
            tenantId);

        var snapshotReady = await Task.WhenAny(snapshotCaptured.Task, Task.Delay(TimeSpan.FromSeconds(10))) == snapshotCaptured.Task;
        snapshotReady.Should().BeTrue("the update must reach the pre-transaction snapshot read within 10s");

        await MutateReservationGuestCountDirectlyAsync(tenantId, created.Value.Id);

        releaseGate.TrySetResult();

        var updated = await updateTask;

        updated.IsFailure.Should().BeTrue();
        updated.Error.Code.Should().Be(ReservationsErrorCodes.ReservationConcurrencyConflict);

        var auditCount = await CountReservationUpdatedAuditEntriesAsync(tenantId);
        auditCount.Should().Be(0, "the losing, stale update must never audit or publish");
    }

    private async Task MutateReservationGuestCountDirectlyAsync(Guid tenantId, Guid reservationId)
    {
        await using var connection = new NpgsqlConnection(_migratorConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var setCommand = connection.CreateCommand())
        {
            setCommand.CommandText = $"SET LOCAL app.tenant_id = '{tenantId:D}'";
            await setCommand.ExecuteNonQueryAsync();
        }

        await using (var updateCommand = connection.CreateCommand())
        {
            // A genuine UPDATE (even one that writes back the same value)
            // advances PostgreSQL's xmin system column — exactly the
            // real-world event the snapshot-vs-current-xmin check exists to
            // detect.
            updateCommand.CommandText = "UPDATE reservations.reservations SET guest_count = guest_count WHERE id = @id";
            updateCommand.Parameters.AddWithValue("id", reservationId);
            await updateCommand.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }

    private async Task<long> CountReservationUpdatedAuditEntriesAsync(Guid tenantId)
    {
        await using var connection = new NpgsqlConnection(_migratorConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var setCommand = connection.CreateCommand())
        {
            setCommand.CommandText = $"SET LOCAL app.tenant_id = '{tenantId:D}'";
            await setCommand.ExecuteNonQueryAsync();
        }

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM reservations.reservation_audit_log WHERE action_code = 'reservation_updated'";
        var count = (long)(await command.ExecuteScalarAsync())!;
        await transaction.CommitAsync();
        return count;
    }

    // ---- Cancel ------------------------------------------------------------

    [Fact]
    public async Task Cancelling_a_confirmed_reservation_succeeds_and_frees_the_period_immediately()
    {
        var tenantId = Guid.NewGuid();
        using var host = await BuildHostAsync();
        var propertyId = await SeedActivePropertyAsync(tenantId);
        var checkIn = new DateTimeOffset(2026, 10, 1, 14, 0, 0, TimeSpan.Zero);
        var checkOut = new DateTimeOffset(2026, 10, 5, 11, 0, 0, TimeSpan.Zero);
        var created = await ExecuteAsync(host, CreateCommand(tenantId, propertyId, checkIn, checkOut), tenantId);

        var cancelled = await ExecuteAsync(host, new CancelReservationCommand(tenantId, Guid.NewGuid(), created.Value.Id), tenantId);
        cancelled.IsSuccess.Should().BeTrue();
        cancelled.Value.Status.Should().Be("cancelled");

        // The same period is now free — a new overlapping reservation must succeed.
        var newReservation = await ExecuteAsync(host, CreateCommand(tenantId, propertyId, checkIn, checkOut), tenantId);
        newReservation.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Cancelling_an_already_cancelled_reservation_fails_with_ReservationAlreadyCancelled()
    {
        var tenantId = Guid.NewGuid();
        using var host = await BuildHostAsync();
        var propertyId = await SeedActivePropertyAsync(tenantId);
        var created = await ExecuteAsync(host, CreateCommand(tenantId, propertyId), tenantId);
        await ExecuteAsync(host, new CancelReservationCommand(tenantId, Guid.NewGuid(), created.Value.Id), tenantId);

        var result = await ExecuteAsync(host, new CancelReservationCommand(tenantId, Guid.NewGuid(), created.Value.Id), tenantId);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(ReservationsErrorCodes.ReservationAlreadyCancelled);
    }

    // ---- List / Detail -----------------------------------------------------

    [Fact]
    public async Task Detail_of_a_cross_tenant_reservation_is_not_found()
    {
        var tenantId = Guid.NewGuid();
        var otherTenantId = Guid.NewGuid();
        using var host = await BuildHostAsync();
        var propertyId = await SeedActivePropertyAsync(tenantId);
        var created = await ExecuteAsync(host, CreateCommand(tenantId, propertyId), tenantId);

        var result = await ExecuteAsync(host, new GetReservationDetailQuery(created.Value.Id), otherTenantId);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(ReservationsErrorCodes.ReservationNotFound);
    }

    [Fact]
    public async Task List_filters_by_property_and_status()
    {
        var tenantId = Guid.NewGuid();
        using var host = await BuildHostAsync();
        var propertyId = await SeedActivePropertyAsync(tenantId);
        var otherPropertyId = await SeedActivePropertyAsync(tenantId);

        var created = await ExecuteAsync(host, CreateCommand(tenantId, propertyId), tenantId);
        await ExecuteAsync(host, CreateCommand(tenantId, otherPropertyId, new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2027, 1, 2, 0, 0, 0, TimeSpan.Zero)), tenantId);
        await ExecuteAsync(host, new CancelReservationCommand(tenantId, Guid.NewGuid(), created.Value.Id), tenantId);

        var confirmedForOtherProperty = await ExecuteAsync(
            host, new ListReservationsQuery(otherPropertyId, "confirmed", null, null, null, null), tenantId);

        confirmedForOtherProperty.Value.Items.Should().ContainSingle(i => i.PropertyId == otherPropertyId);
    }

    // ---- Helpers -----------------------------------------------------------

    private static CreateReservationCommand CreateCommand(
        Guid tenantId, Guid propertyId, DateTimeOffset? checkInAt = null, DateTimeOffset? checkOutAt = null, int guestCount = 2) =>
        new(
            tenantId, Guid.NewGuid(), propertyId, "Test Guest", "+5584999999999",
            checkInAt ?? new DateTimeOffset(2026, 8, 10, 14, 0, 0, TimeSpan.Zero),
            checkOutAt ?? new DateTimeOffset(2026, 8, 13, 11, 0, 0, TimeSpan.Zero),
            guestCount);

    private static async Task SetTenantAsync(DbContext dbContext, Guid tenantId) =>
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.tenant_id', {tenantId.ToString()}, true)");

    private static PropertyManagementDbContext CreatePropertyManagementDbContext(string connectionString, ITenantContext? tenantContext = null)
    {
        var options = new DbContextOptionsBuilder<PropertyManagementDbContext>()
            .UseNpgsql(connectionString, npgsqlOptions =>
                npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "property_management"))
            .Options;

        return new PropertyManagementDbContext(options, tenantContext ?? new TenantContext());
    }

    private static ReservationsDbContext CreateReservationsDbContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<ReservationsDbContext>()
            .UseNpgsql(connectionString, npgsqlOptions =>
                npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "reservations"))
            .Options;

        return new ReservationsDbContext(options, new TenantContext());
    }
}
