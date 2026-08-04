using FluentAssertions;
using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.BuildingBlocks.Infrastructure.Messaging;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.BuildingBlocks.Infrastructure.Persistence;
using IHostPro.Contexts.PropertyManagement.Application;
using IHostPro.Contexts.PropertyManagement.Application.Errors;
using IHostPro.Contexts.PropertyManagement.Application.Properties;
using IHostPro.Contexts.PropertyManagement.Domain;
using IHostPro.Contexts.PropertyManagement.Domain.Enums;
using IHostPro.Contexts.PropertyManagement.Infrastructure;
using IHostPro.Contexts.PropertyManagement.Infrastructure.Persistence;
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

namespace IHostPro.Contexts.PropertyManagement.Tests.Integration;

/// <summary>
/// End-to-end test of the Activate/Deactivate/ArchiveProperty use cases
/// against a real PostgreSQL instance (Fase 2, Incremento 1, Checkpoint 4
/// plan) — mirrors <c>PropertyCommandHandlerTests</c>'s structure exactly,
/// dispatching through the REAL production composition root via
/// <see cref="ISender"/>.
/// </summary>
public class PropertyLifecycleCommandHandlerTests : IClassFixture<PropertyLifecycleCommandHandlerTests.Fixture>
{
    private const string OutboxSchema = "property_management_messaging";

    private readonly string _migratorConnectionString;
    private readonly string _appConnectionString;

    public PropertyLifecycleCommandHandlerTests(Fixture fixture)
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

            await using (var migratorDbContext = CreateDbContext(MigratorConnectionString, new TenantContext()))
            {
                await migratorDbContext.Database.MigrateAsync();
            }

            await ProvisionOutboxAsMigratorAsync();
        }

        public async Task DisposeAsync() => await _container.DisposeAsync();

        private async Task ProvisionOutboxAsMigratorAsync()
        {
            var hostBuilder = Host.CreateApplicationBuilder();
            hostBuilder.UseWolverine(opts =>
            {
                opts.EnrollAncillaryPostgresqlOutbox(MigratorConnectionString, OutboxSchema, typeof(PropertyManagementDbContext));
                opts.AutoBuildMessageStorageOnStartup = AutoCreate.None;
                opts.UseEntityFrameworkCoreTransactions();
            });

            using var outboxHost = hostBuilder.Build();
            await outboxHost.SetupResources();

            await using var connection = new NpgsqlConnection(MigratorConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                GRANT USAGE ON SCHEMA {OutboxSchema} TO ihostpro_app;
                GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA {OutboxSchema} TO ihostpro_app;
                GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA {OutboxSchema} TO ihostpro_app;
                ALTER DEFAULT PRIVILEGES FOR ROLE ihostpro_migrator IN SCHEMA {OutboxSchema}
                  GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO ihostpro_app;
                ALTER DEFAULT PRIVILEGES FOR ROLE ihostpro_migrator IN SCHEMA {OutboxSchema}
                  GRANT USAGE, SELECT ON SEQUENCES TO ihostpro_app;
                """;
            await command.ExecuteNonQueryAsync();
        }
    }

    private static readonly PropertyAddressInput SomeAddress = new(
        "59090-000", "Rua Exemplo", "100", "Bloco A", "Ponta Negra", "Natal", "RN", "BR");

    // ---- Service graph (real composition root) -------------------------------

    private async Task<IHost> BuildHostAsync(Action<IServiceCollection>? overrides = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:PropertyManagement"] = _appConnectionString,
        }).Build();

        var hostBuilder = Host.CreateApplicationBuilder();
        hostBuilder.Services.AddScoped<ITenantContext, TenantContext>();
        hostBuilder.Services.AddScoped<ICurrentTenantProvider, TenantContextCurrentTenantProvider>();
        hostBuilder.Services.AddIHostProTenantAwarePipeline();
        hostBuilder.Services.AddPropertyManagementModule(configuration);
        hostBuilder.Services.AddPropertyManagementCommandDispatch();

        overrides?.Invoke(hostBuilder.Services);

        hostBuilder.UseWolverine(opts =>
        {
            opts.EnrollAncillaryPostgresqlOutbox(_appConnectionString, OutboxSchema, typeof(PropertyManagementDbContext));
            opts.AutoBuildMessageStorageOnStartup = AutoCreate.None;
            opts.UseEntityFrameworkCoreTransactions();
        });

        var host = hostBuilder.Build();
        await host.StartAsync();
        return host;
    }

    private static async Task<Result<TResponse>> ExecuteAsync<TMessage, TResponse>(IHost host, TMessage message, Guid tenantId)
        where TMessage : IRequest<Result<TResponse>>
    {
        using var scope = host.Services.CreateScope();
        var sp = scope.ServiceProvider;
        sp.GetRequiredService<ITenantContext>().SetTenant(tenantId);

        return await sp.GetRequiredService<ISender>().Send(message, CancellationToken.None);
    }

    private static async Task<Guid> SeedPropertyAsync(IHost host, Guid tenantId, string code, Guid? condominiumId = null, PropertyAddressInput? address = null)
    {
        var command = new CreatePropertyCommand(
            tenantId, Guid.NewGuid(), code, $"Property {code}", 2, condominiumId, address ?? (condominiumId is null ? SomeAddress : null));
        var created = await ExecuteAsync<CreatePropertyCommand, PropertyResult>(host, command, tenantId);
        return created.Value.Id;
    }

    private static async Task<Guid> SeedActivePropertyAsync(IHost host, Guid tenantId, string code)
    {
        var propertyId = await SeedPropertyAsync(host, tenantId, code);
        await ExecuteAsync<ActivatePropertyCommand, PropertyResult>(host, new ActivatePropertyCommand(tenantId, Guid.NewGuid(), propertyId), tenantId);
        return propertyId;
    }

    private static async Task<Guid> SeedInactivePropertyAsync(IHost host, Guid tenantId, string code)
    {
        var propertyId = await SeedActivePropertyAsync(host, tenantId, code);
        await ExecuteAsync<DeactivatePropertyCommand, PropertyResult>(host, new DeactivatePropertyCommand(tenantId, Guid.NewGuid(), propertyId), tenantId);
        return propertyId;
    }

    private static async Task<Guid> SeedArchivedPropertyAsync(IHost host, Guid tenantId, string code)
    {
        var propertyId = await SeedPropertyAsync(host, tenantId, code);
        await ExecuteAsync<ArchivePropertyCommand, PropertyResult>(host, new ArchivePropertyCommand(tenantId, Guid.NewGuid(), propertyId), tenantId);
        return propertyId;
    }

    // ---- Permitted transitions -------------------------------------------------

    [Fact]
    public async Task Draft_activates_to_Active_and_persists_status_and_UpdatedAt()
    {
        var tenantId = Guid.NewGuid();
        using var host = await BuildHostAsync();
        var propertyId = await SeedPropertyAsync(host, tenantId, "LC-1");

        var result = await ExecuteAsync<ActivatePropertyCommand, PropertyResult>(
            host, new ActivatePropertyCommand(tenantId, Guid.NewGuid(), propertyId), tenantId);

        result.IsSuccess.Should().BeTrue();

        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);
        var property = await dbContext.Properties.SingleAsync(p => p.Id == propertyId);
        property.Status.Should().Be(PropertyStatus.Active);
        // Postgres timestamptz has microsecond precision vs. .NET's 100ns
        // ticks — a tolerance avoids a false failure on the round-trip.
        property.UpdatedAt.Should().BeCloseTo(result.Value.UpdatedAt, TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public async Task Active_deactivates_to_Inactive()
    {
        var tenantId = Guid.NewGuid();
        using var host = await BuildHostAsync();
        var propertyId = await SeedActivePropertyAsync(host, tenantId, "LC-2");

        var result = await ExecuteAsync<DeactivatePropertyCommand, PropertyResult>(
            host, new DeactivatePropertyCommand(tenantId, Guid.NewGuid(), propertyId), tenantId);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be("inactive");
    }

    [Fact]
    public async Task Inactive_activates_back_to_Active()
    {
        var tenantId = Guid.NewGuid();
        using var host = await BuildHostAsync();
        var propertyId = await SeedInactivePropertyAsync(host, tenantId, "LC-3");

        var result = await ExecuteAsync<ActivatePropertyCommand, PropertyResult>(
            host, new ActivatePropertyCommand(tenantId, Guid.NewGuid(), propertyId), tenantId);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be("active");
    }

    [Fact]
    public async Task Draft_archives_directly()
    {
        var tenantId = Guid.NewGuid();
        using var host = await BuildHostAsync();
        var propertyId = await SeedPropertyAsync(host, tenantId, "LC-4");

        var result = await ExecuteAsync<ArchivePropertyCommand, PropertyResult>(
            host, new ArchivePropertyCommand(tenantId, Guid.NewGuid(), propertyId), tenantId);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be("archived");
    }

    [Fact]
    public async Task Inactive_archives()
    {
        var tenantId = Guid.NewGuid();
        using var host = await BuildHostAsync();
        var propertyId = await SeedInactivePropertyAsync(host, tenantId, "LC-5");

        var result = await ExecuteAsync<ArchivePropertyCommand, PropertyResult>(
            host, new ArchivePropertyCommand(tenantId, Guid.NewGuid(), propertyId), tenantId);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be("archived");
    }

    // ---- Prohibited transitions -------------------------------------------------

    [Fact]
    public async Task Active_cannot_archive_directly()
    {
        var tenantId = Guid.NewGuid();
        using var host = await BuildHostAsync();
        var propertyId = await SeedActivePropertyAsync(host, tenantId, "LC-6");

        var result = await ExecuteAsync<ArchivePropertyCommand, PropertyResult>(
            host, new ArchivePropertyCommand(tenantId, Guid.NewGuid(), propertyId), tenantId);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(PropertyManagementErrorCodes.InvalidPropertyStatusTransition);
    }

    [Fact]
    public async Task Draft_cannot_deactivate()
    {
        var tenantId = Guid.NewGuid();
        using var host = await BuildHostAsync();
        var propertyId = await SeedPropertyAsync(host, tenantId, "LC-7");

        var result = await ExecuteAsync<DeactivatePropertyCommand, PropertyResult>(
            host, new DeactivatePropertyCommand(tenantId, Guid.NewGuid(), propertyId), tenantId);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(PropertyManagementErrorCodes.InvalidPropertyStatusTransition);
    }

    [Fact]
    public async Task Archived_rejects_every_transition()
    {
        var tenantId = Guid.NewGuid();
        using var host = await BuildHostAsync();
        var propertyId = await SeedArchivedPropertyAsync(host, tenantId, "LC-8");

        var activate = await ExecuteAsync<ActivatePropertyCommand, PropertyResult>(
            host, new ActivatePropertyCommand(tenantId, Guid.NewGuid(), propertyId), tenantId);
        var deactivate = await ExecuteAsync<DeactivatePropertyCommand, PropertyResult>(
            host, new DeactivatePropertyCommand(tenantId, Guid.NewGuid(), propertyId), tenantId);
        var archive = await ExecuteAsync<ArchivePropertyCommand, PropertyResult>(
            host, new ArchivePropertyCommand(tenantId, Guid.NewGuid(), propertyId), tenantId);

        activate.Error.Code.Should().Be(PropertyManagementErrorCodes.PropertyAlreadyArchived);
        deactivate.Error.Code.Should().Be(PropertyManagementErrorCodes.PropertyAlreadyArchived);
        archive.Error.Code.Should().Be(PropertyManagementErrorCodes.PropertyAlreadyArchived);
    }

    // ---- Activation and effective address --------------------------------------

    [Fact]
    public async Task Activation_with_own_address_succeeds()
    {
        var tenantId = Guid.NewGuid();
        using var host = await BuildHostAsync();
        var propertyId = await SeedPropertyAsync(host, tenantId, "LC-9");

        var result = await ExecuteAsync<ActivatePropertyCommand, PropertyResult>(
            host, new ActivatePropertyCommand(tenantId, Guid.NewGuid(), propertyId), tenantId);

        result.IsSuccess.Should().BeTrue();
        result.Value.EffectiveAddressSource.Should().Be("property");
    }

    [Fact]
    public async Task Activation_with_inherited_condominium_address_succeeds()
    {
        var tenantId = Guid.NewGuid();
        using var host = await BuildHostAsync();
        var condominiumId = await SeedCondominiumAsync(host, tenantId);
        var propertyId = await SeedPropertyAsync(host, tenantId, "LC-10", condominiumId);

        var result = await ExecuteAsync<ActivatePropertyCommand, PropertyResult>(
            host, new ActivatePropertyCommand(tenantId, Guid.NewGuid(), propertyId), tenantId);

        result.IsSuccess.Should().BeTrue();
        result.Value.EffectiveAddressSource.Should().Be("condominium");
    }

    private static readonly IHostPro.Contexts.PropertyManagement.Application.Condominiums.CondominiumAddressInput SomeCondominiumAddress = new(
        "59090-100", "Rua do Condomínio", "1", null, "Ponta Negra", "Natal", "RN", "BR");

    private static async Task<Guid> SeedCondominiumAsync(IHost host, Guid tenantId)
    {
        var created = await ExecuteAsync<IHostPro.Contexts.PropertyManagement.Application.Condominiums.CreateCondominiumCommand,
            IHostPro.Contexts.PropertyManagement.Application.Condominiums.CondominiumResult>(
            host,
            new IHostPro.Contexts.PropertyManagement.Application.Condominiums.CreateCondominiumCommand(
                tenantId, Guid.NewGuid(), "Condomínio Exemplo", SomeCondominiumAddress),
            tenantId);
        return created.Value.Id;
    }

    // ---- RLS / tenant isolation -------------------------------------------------

    [Fact]
    public async Task A_property_is_invisible_to_a_different_tenant_for_lifecycle_transitions()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        using var host = await BuildHostAsync();
        var propertyId = await SeedPropertyAsync(host, tenantA, "LC-11");

        var result = await ExecuteAsync<ActivatePropertyCommand, PropertyResult>(
            host, new ActivatePropertyCommand(tenantB, Guid.NewGuid(), propertyId), tenantB);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(PropertyManagementErrorCodes.PropertyNotFound);
    }

    [Fact]
    public async Task Absent_tenant_context_fails_closed()
    {
        using var host = await BuildHostAsync();
        using var scope = host.Services.CreateScope();

        var act = async () => await scope.ServiceProvider.GetRequiredService<ISender>()
            .Send(new ActivatePropertyCommand(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()), CancellationToken.None);

        await act.Should().ThrowAsync<TenantContextNotResolvedException>();
    }

    // ---- Rollback -----------------------------------------------------------

    private sealed class ThrowingPropertyAuditWriter : IPropertyAuditWriter
    {
        public void Record(PropertyAuditEntry entry) =>
            throw new InvalidOperationException("Simulated failure after Property.Activate() was staged.");
    }

    [Fact]
    public async Task A_failure_after_activation_was_staged_rolls_back_with_no_partial_state()
    {
        var tenantId = Guid.NewGuid();
        using var seedHost = await BuildHostAsync();
        var propertyId = await SeedPropertyAsync(seedHost, tenantId, "LC-12");

        using var host = await BuildHostAsync(overrides: sc =>
            sc.AddScoped<IPropertyAuditWriter, ThrowingPropertyAuditWriter>());

        var act = async () => await ExecuteAsync<ActivatePropertyCommand, PropertyResult>(
            host, new ActivatePropertyCommand(tenantId, Guid.NewGuid(), propertyId), tenantId);

        await act.Should().ThrowAsync<InvalidOperationException>();

        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);
        var property = await dbContext.Properties.SingleAsync(p => p.Id == propertyId);
        property.Status.Should().Be(PropertyStatus.Draft); // unchanged
        (await CountEnvelopesByMessageTypeAsync("IHostPro.Contexts.PropertyManagement.Contracts.PropertyActivated")).Should().Be(0);
    }

    // ---- Archived blocks cadastral update ----------------------------------------

    [Fact]
    public async Task An_archived_property_rejects_a_cadastral_update()
    {
        var tenantId = Guid.NewGuid();
        using var host = await BuildHostAsync();
        var propertyId = await SeedArchivedPropertyAsync(host, tenantId, "LC-13");

        var update = new UpdatePropertyCommand(
            tenantId, Guid.NewGuid(), propertyId,
            Optional<string>.Unset, Optional<string>.Of("New Name"), Optional<int>.Unset,
            Optional<Guid?>.Unset, Optional<PropertyAddressInput?>.Unset);
        var result = await ExecuteAsync<UpdatePropertyCommand, PropertyResult>(host, update, tenantId);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(PropertyManagementErrorCodes.ArchivedPropertyCannotBeModified);
    }

    // ---- Real concurrency ---------------------------------------------------

    private sealed class BarrierPropertyAuditWriter : IPropertyAuditWriter
    {
        private readonly Barrier _barrier;

        public BarrierPropertyAuditWriter(Barrier barrier) => _barrier = barrier;

        public void Record(PropertyAuditEntry entry) => _barrier.SignalAndWait(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Two_concurrent_activations_of_the_same_draft_allow_only_one_to_succeed_and_translate_to_PropertyConcurrencyConflict()
    {
        var tenantId = Guid.NewGuid();
        using var seedHost = await BuildHostAsync();
        var propertyId = await SeedPropertyAsync(seedHost, tenantId, "LC-14");

        using var barrier = new Barrier(2);
        using var hostA = await BuildHostAsync(overrides: sc =>
            sc.AddScoped<IPropertyAuditWriter>(_ => new BarrierPropertyAuditWriter(barrier)));
        using var hostB = await BuildHostAsync(overrides: sc =>
            sc.AddScoped<IPropertyAuditWriter>(_ => new BarrierPropertyAuditWriter(barrier)));

        var taskA = ExecuteAsync<ActivatePropertyCommand, PropertyResult>(
            hostA, new ActivatePropertyCommand(tenantId, Guid.NewGuid(), propertyId), tenantId);
        var taskB = ExecuteAsync<ActivatePropertyCommand, PropertyResult>(
            hostB, new ActivatePropertyCommand(tenantId, Guid.NewGuid(), propertyId), tenantId);
        var results = await Task.WhenAll(taskA, taskB);

        results.Count(r => r.IsSuccess).Should().Be(1);
        var failure = results.Single(r => r.IsFailure);
        failure.Error.Code.Should().Be(PropertyManagementErrorCodes.PropertyConcurrencyConflict);
    }

    /// <summary>
    /// No <see cref="Barrier"/> here, deliberately: <c>Archive</c>'s status
    /// check runs immediately after the read, before either command reaches
    /// any shared synchronization point a barrier could pair against —
    /// forcing one would either deadlock (if Archive's early rejection never
    /// signals it) or artificially manufacture a race neither command's own
    /// logic actually contains. The invariant under test is a structural
    /// state-machine rule, not an xmin race: <c>Archive</c> is only ever
    /// valid from <c>Draft</c>/<c>Inactive</c>, so a direct <c>Active</c> →
    /// <c>Archived</c> confirmation must never happen — regardless of how
    /// the two requests interleave, either Archive is rejected outright
    /// (reading the still-<c>Active</c> row), or it succeeds only because
    /// Deactivate's commit was already visible first (reading
    /// <c>Inactive</c>, a legitimately different starting state).
    /// </summary>
    [Fact]
    public async Task An_active_property_receiving_concurrent_Deactivate_and_Archive_never_confirms_Active_to_Archived_directly()
    {
        var tenantId = Guid.NewGuid();
        using var host = await BuildHostAsync();
        var propertyId = await SeedActivePropertyAsync(host, tenantId, "LC-15");

        var deactivateTask = ExecuteAsync<DeactivatePropertyCommand, PropertyResult>(
            host, new DeactivatePropertyCommand(tenantId, Guid.NewGuid(), propertyId), tenantId);
        var archiveTask = ExecuteAsync<ArchivePropertyCommand, PropertyResult>(
            host, new ArchivePropertyCommand(tenantId, Guid.NewGuid(), propertyId), tenantId);
        var results = await Task.WhenAll(deactivateTask, archiveTask);
        var deactivateResult = results[0];
        var archiveResult = results[1];

        if (archiveResult.IsSuccess)
        {
            // Archive only legitimately succeeded via Inactive as an
            // intermediate — proof Deactivate committed first, never a
            // direct Active → Archived jump.
            deactivateResult.IsSuccess.Should().BeTrue();
        }
        else
        {
            archiveResult.Error.Code.Should().Be(PropertyManagementErrorCodes.InvalidPropertyStatusTransition);
        }

        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);
        var property = await dbContext.Properties.SingleAsync(p => p.Id == propertyId);
        property.Status.Should().BeOneOf(PropertyStatus.Inactive, PropertyStatus.Archived); // never anything else
    }

    [Fact]
    public async Task Concurrent_activation_and_cadastral_update_of_an_inactive_property_allow_only_one_to_confirm_based_on_the_original_version()
    {
        var tenantId = Guid.NewGuid();
        using var seedHost = await BuildHostAsync();
        var propertyId = await SeedInactivePropertyAsync(seedHost, tenantId, "LC-16");

        using var barrier = new Barrier(2);
        using var hostA = await BuildHostAsync(overrides: sc =>
            sc.AddScoped<IPropertyAuditWriter>(_ => new BarrierPropertyAuditWriter(barrier)));
        using var hostB = await BuildHostAsync(overrides: sc =>
            sc.AddScoped<IPropertyAuditWriter>(_ => new BarrierPropertyAuditWriter(barrier)));

        var activateTask = ExecuteAsync<ActivatePropertyCommand, PropertyResult>(
            hostA, new ActivatePropertyCommand(tenantId, Guid.NewGuid(), propertyId), tenantId);
        var update = new UpdatePropertyCommand(
            tenantId, Guid.NewGuid(), propertyId,
            Optional<string>.Unset, Optional<string>.Of("Updated Name"), Optional<int>.Unset,
            Optional<Guid?>.Unset, Optional<PropertyAddressInput?>.Unset);
        var updateTask = ExecuteAsync<UpdatePropertyCommand, PropertyResult>(hostB, update, tenantId);

        var results = await Task.WhenAll(activateTask, updateTask);

        results.Count(r => r.IsSuccess).Should().Be(1);
        var failure = results.Single(r => r.IsFailure);
        failure.Error.Code.Should().Be(PropertyManagementErrorCodes.PropertyConcurrencyConflict);
    }

    // ---- Helpers ---------------------------------------------------------------

    private async Task<long> CountEnvelopesByMessageTypeAsync(string messageType)
    {
        await using var connection = new NpgsqlConnection(_migratorConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT count(*) FROM {OutboxSchema}.wolverine_outgoing_envelopes WHERE message_type = @messageType";
        command.Parameters.AddWithValue("messageType", messageType);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task SetPostgresTenantAsync(PropertyManagementDbContext dbContext, Guid tenantId) =>
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.tenant_id', {tenantId.ToString()}, true)");

    private PropertyManagementDbContext CreateMigratorDbContextWithTenant(Guid tenantId)
    {
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);
        return CreateDbContext(_migratorConnectionString, tenantContext);
    }

    private static PropertyManagementDbContext CreateDbContext(string connectionString, ITenantContext tenantContext)
    {
        var options = new DbContextOptionsBuilder<PropertyManagementDbContext>()
            .UseNpgsql(connectionString, npgsqlOptions =>
                npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "property_management"))
            .Options;

        return new PropertyManagementDbContext(options, tenantContext);
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
