using FluentAssertions;
using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.BuildingBlocks.Infrastructure.Messaging;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.BuildingBlocks.Infrastructure.Persistence;
using IHostPro.Contexts.PropertyManagement.Application;
using IHostPro.Contexts.PropertyManagement.Application.Condominiums;
using IHostPro.Contexts.PropertyManagement.Application.Errors;
using IHostPro.Contexts.PropertyManagement.Application.Properties;
using IHostPro.Contexts.PropertyManagement.Domain;
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
/// End-to-end test of the Create/UpdateProperty use cases against a real
/// PostgreSQL instance (Fase 2, Incremento 1, Checkpoint 3 plan) — mirrors
/// <c>CondominiumCommandHandlerTests</c>'s structure exactly, dispatching
/// through the REAL production composition root
/// (<c>AddPropertyManagementModule</c>/<c>AddPropertyManagementCommandDispatch</c>
/// via <see cref="ISender"/>).
/// </summary>
public class PropertyCommandHandlerTests : IClassFixture<PropertyCommandHandlerTests.Fixture>
{
    private const string OutboxSchema = "property_management_messaging";

    private readonly string _migratorConnectionString;
    private readonly string _appConnectionString;

    public PropertyCommandHandlerTests(Fixture fixture)
    {
        _migratorConnectionString = fixture.MigratorConnectionString;
        _appConnectionString = fixture.AppConnectionString;
    }

    /// <summary>Started once per test class — see <c>IdentityRowLevelSecurityTests.Fixture</c>'s doc comment for the full rationale.</summary>
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

    private static readonly CondominiumAddressInput SomeCondominiumAddress = new(
        "59090-100", "Rua do Condomínio", "1", null, "Ponta Negra", "Natal", "RN", "BR");

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

    private static async Task<Guid> SeedCondominiumAsync(IHost host, Guid tenantId)
    {
        var created = await ExecuteAsync<CreateCondominiumCommand, CondominiumResult>(
            host, new CreateCondominiumCommand(tenantId, Guid.NewGuid(), "Condomínio Exemplo", SomeCondominiumAddress), tenantId);
        return created.Value.Id;
    }

    // ---- Tests: Create happy path / same-commit guarantees ----------------

    /// <summary>
    /// Envelope-in-the-same-commit is verified separately in
    /// <c>PropertyIntegrationEventsTests</c> — a route-less host here would
    /// make <c>IDbContextOutbox.PublishAsync</c> a no-op, mirrors
    /// <c>CondominiumCommandHandlerTests</c>'s own scope split.
    /// </summary>
    [Fact]
    public async Task Creating_a_property_with_own_address_persists_it_and_exactly_one_audit_entry_in_the_same_commit()
    {
        var tenantId = Guid.NewGuid();
        using var host = await BuildHostAsync();

        var command = new CreatePropertyCommand(tenantId, Guid.NewGuid(), "STUDIO-1", "Studio 1", 2, null, SomeAddress);
        var result = await ExecuteAsync<CreatePropertyCommand, PropertyResult>(host, command, tenantId);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be("draft");

        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);

        (await dbContext.Properties.CountAsync(p => p.Id == result.Value.Id)).Should().Be(1);
        (await dbContext.PropertyAuditLog.CountAsync(e => e.AggregateId == result.Value.Id)).Should().Be(1);
    }

    [Fact]
    public async Task Creating_a_property_with_a_condominium_and_no_own_address_persists_it()
    {
        var tenantId = Guid.NewGuid();
        using var host = await BuildHostAsync();
        var condominiumId = await SeedCondominiumAsync(host, tenantId);

        var command = new CreatePropertyCommand(tenantId, Guid.NewGuid(), "STUDIO-2", "Studio 2", 2, condominiumId, null);
        var result = await ExecuteAsync<CreatePropertyCommand, PropertyResult>(host, command, tenantId);

        result.IsSuccess.Should().BeTrue();
        result.Value.EffectiveAddressSource.Should().Be("condominium");

        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);

        (await dbContext.Properties.CountAsync(p => p.Id == result.Value.Id)).Should().Be(1);
    }

    [Fact]
    public async Task Updating_a_property_persists_the_change_and_exactly_one_audit_entry_in_the_same_commit()
    {
        var tenantId = Guid.NewGuid();
        using var host = await BuildHostAsync();
        var created = await ExecuteAsync<CreatePropertyCommand, PropertyResult>(
            host, new CreatePropertyCommand(tenantId, Guid.NewGuid(), "STUDIO-3", "Original", 2, null, SomeAddress), tenantId);

        var update = new UpdatePropertyCommand(
            tenantId, Guid.NewGuid(), created.Value.Id,
            Optional<string>.Unset, Optional<string>.Of("Updated"), Optional<int>.Unset,
            Optional<Guid?>.Unset, Optional<PropertyAddressInput?>.Unset);
        var result = await ExecuteAsync<UpdatePropertyCommand, PropertyResult>(host, update, tenantId);

        result.IsSuccess.Should().BeTrue();

        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);

        (await dbContext.Properties.Where(p => p.Id == created.Value.Id).Select(p => p.Name).SingleAsync()).Should().Be("Updated");
        (await dbContext.PropertyAuditLog.CountAsync(e => e.AggregateId == created.Value.Id && e.ActionCode == "property_updated")).Should().Be(1);
    }

    [Fact]
    public async Task A_no_op_update_persists_no_audit_entry_and_no_outbox_envelope()
    {
        var tenantId = Guid.NewGuid();
        using var host = await BuildHostAsync();
        var created = await ExecuteAsync<CreatePropertyCommand, PropertyResult>(
            host, new CreatePropertyCommand(tenantId, Guid.NewGuid(), "STUDIO-4", "Same Name", 2, null, SomeAddress), tenantId);

        var update = new UpdatePropertyCommand(
            tenantId, Guid.NewGuid(), created.Value.Id,
            Optional<string>.Unset, Optional<string>.Of("Same Name"), Optional<int>.Unset,
            Optional<Guid?>.Unset, Optional<PropertyAddressInput?>.Unset);
        var result = await ExecuteAsync<UpdatePropertyCommand, PropertyResult>(host, update, tenantId);

        result.IsSuccess.Should().BeTrue();

        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);

        (await dbContext.PropertyAuditLog.CountAsync(e => e.AggregateId == created.Value.Id && e.ActionCode == "property_updated")).Should().Be(0);
        (await CountEnvelopesByMessageTypeAsync("IHostPro.Contexts.PropertyManagement.Contracts.PropertyUpdated")).Should().Be(0);
    }

    // ---- Tests: code uniqueness -----------------------------------------------

    [Fact]
    public async Task Creating_two_properties_with_the_same_normalized_code_in_the_same_tenant_fails_with_PropertyCodeAlreadyExists()
    {
        var tenantId = Guid.NewGuid();
        using var host = await BuildHostAsync();
        await ExecuteAsync<CreatePropertyCommand, PropertyResult>(
            host, new CreatePropertyCommand(tenantId, Guid.NewGuid(), "STUDIO-5", "First", 2, null, SomeAddress), tenantId);

        var result = await ExecuteAsync<CreatePropertyCommand, PropertyResult>(
            host, new CreatePropertyCommand(tenantId, Guid.NewGuid(), "studio-5", "Second", 2, null, SomeAddress), tenantId);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(PropertyManagementErrorCodes.PropertyCodeAlreadyExists);
    }

    [Fact]
    public async Task The_same_normalized_code_is_allowed_across_different_tenants()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        using var host = await BuildHostAsync();
        await ExecuteAsync<CreatePropertyCommand, PropertyResult>(
            host, new CreatePropertyCommand(tenantA, Guid.NewGuid(), "STUDIO-6", "Tenant A's", 2, null, SomeAddress), tenantA);

        var result = await ExecuteAsync<CreatePropertyCommand, PropertyResult>(
            host, new CreatePropertyCommand(tenantB, Guid.NewGuid(), "STUDIO-6", "Tenant B's", 2, null, SomeAddress), tenantB);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Updating_a_property_to_its_own_code_with_different_casing_is_a_no_op_without_conflict()
    {
        var tenantId = Guid.NewGuid();
        using var host = await BuildHostAsync();
        var created = await ExecuteAsync<CreatePropertyCommand, PropertyResult>(
            host, new CreatePropertyCommand(tenantId, Guid.NewGuid(), "STUDIO-7", "Studio 7", 2, null, SomeAddress), tenantId);

        var update = new UpdatePropertyCommand(
            tenantId, Guid.NewGuid(), created.Value.Id,
            Optional<string>.Of("studio-7"), Optional<string>.Unset, Optional<int>.Unset,
            Optional<Guid?>.Unset, Optional<PropertyAddressInput?>.Unset);
        var result = await ExecuteAsync<UpdatePropertyCommand, PropertyResult>(host, update, tenantId);

        result.IsSuccess.Should().BeTrue();

        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);
        (await dbContext.PropertyAuditLog.CountAsync(e => e.AggregateId == created.Value.Id && e.ActionCode == "property_updated")).Should().Be(0);
    }

    // ---- Tests: RLS / tenant isolation --------------------------------------

    [Fact]
    public async Task A_property_is_invisible_to_a_different_tenant()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        using var host = await BuildHostAsync();
        var created = await ExecuteAsync<CreatePropertyCommand, PropertyResult>(
            host, new CreatePropertyCommand(tenantA, Guid.NewGuid(), "STUDIO-8", "Tenant A's", 2, null, SomeAddress), tenantA);

        var result = await ExecuteAsync<GetPropertyDetailQuery, PropertyResult>(host, new GetPropertyDetailQuery(created.Value.Id), tenantB);

        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task Absent_tenant_context_fails_closed()
    {
        using var host = await BuildHostAsync();
        using var scope = host.Services.CreateScope();

        // ITenantContext.SetTenant is never called on this scope.
        var act = async () => await scope.ServiceProvider.GetRequiredService<ISender>()
            .Send(new CreatePropertyCommand(Guid.NewGuid(), Guid.NewGuid(), "STUDIO-9", "X", 2, null, SomeAddress), CancellationToken.None);

        await act.Should().ThrowAsync<TenantContextNotResolvedException>();
    }

    // ---- Tests: rollback -----------------------------------------------------

    private sealed class ThrowingPropertyAuditWriter : IPropertyAuditWriter
    {
        public void Record(PropertyAuditEntry entry) =>
            throw new InvalidOperationException("Simulated failure after Property.Create() was staged.");
    }

    [Fact]
    public async Task A_failure_after_the_property_was_staged_rolls_back_the_entire_transaction_with_no_partial_state()
    {
        var tenantId = Guid.NewGuid();
        using var host = await BuildHostAsync(overrides: sc =>
            sc.AddScoped<IPropertyAuditWriter, ThrowingPropertyAuditWriter>());

        var act = async () => await ExecuteAsync<CreatePropertyCommand, PropertyResult>(
            host, new CreatePropertyCommand(tenantId, Guid.NewGuid(), "STUDIO-10", "X", 2, null, SomeAddress), tenantId);

        await act.Should().ThrowAsync<InvalidOperationException>();

        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);

        (await dbContext.Properties.CountAsync()).Should().Be(0);
        (await CountEnvelopesByMessageTypeAsync("IHostPro.Contexts.PropertyManagement.Contracts.PropertyCreated")).Should().Be(0);
    }

    // ---- Tests: real concurrency ---------------------------------------------

    private sealed class BarrierPropertyAuditWriter : IPropertyAuditWriter
    {
        private readonly Barrier _barrier;

        public BarrierPropertyAuditWriter(Barrier barrier) => _barrier = barrier;

        public void Record(PropertyAuditEntry entry) => _barrier.SignalAndWait(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Two_concurrent_updates_of_the_same_property_allow_only_one_to_succeed_and_translate_to_PropertyConcurrencyConflict_without_retry()
    {
        var tenantId = Guid.NewGuid();
        using var seedHost = await BuildHostAsync();
        var created = await ExecuteAsync<CreatePropertyCommand, PropertyResult>(
            seedHost, new CreatePropertyCommand(tenantId, Guid.NewGuid(), "STUDIO-11", "Original", 2, null, SomeAddress), tenantId);

        using var barrier = new Barrier(2);
        using var hostA = await BuildHostAsync(overrides: sc =>
            sc.AddScoped<IPropertyAuditWriter>(_ => new BarrierPropertyAuditWriter(barrier)));
        using var hostB = await BuildHostAsync(overrides: sc =>
            sc.AddScoped<IPropertyAuditWriter>(_ => new BarrierPropertyAuditWriter(barrier)));

        var updateA = new UpdatePropertyCommand(
            tenantId, Guid.NewGuid(), created.Value.Id,
            Optional<string>.Unset, Optional<string>.Of("Name A"), Optional<int>.Unset,
            Optional<Guid?>.Unset, Optional<PropertyAddressInput?>.Unset);
        var updateB = new UpdatePropertyCommand(
            tenantId, Guid.NewGuid(), created.Value.Id,
            Optional<string>.Unset, Optional<string>.Of("Name B"), Optional<int>.Unset,
            Optional<Guid?>.Unset, Optional<PropertyAddressInput?>.Unset);

        var taskA = ExecuteAsync<UpdatePropertyCommand, PropertyResult>(hostA, updateA, tenantId);
        var taskB = ExecuteAsync<UpdatePropertyCommand, PropertyResult>(hostB, updateB, tenantId);
        var results = await Task.WhenAll(taskA, taskB);

        results.Count(r => r.IsSuccess).Should().Be(1);
        var failure = results.Single(r => r.IsFailure);
        failure.Error.Code.Should().Be(PropertyManagementErrorCodes.PropertyConcurrencyConflict);
    }

    [Fact]
    public async Task Two_concurrent_creations_with_the_same_normalized_code_allow_only_one_to_succeed_and_translate_to_PropertyCodeAlreadyExists()
    {
        var tenantId = Guid.NewGuid();

        using var barrier = new Barrier(2);
        using var hostA = await BuildHostAsync(overrides: sc =>
            sc.AddScoped<IPropertyAuditWriter>(_ => new BarrierPropertyAuditWriter(barrier)));
        using var hostB = await BuildHostAsync(overrides: sc =>
            sc.AddScoped<IPropertyAuditWriter>(_ => new BarrierPropertyAuditWriter(barrier)));

        var commandA = new CreatePropertyCommand(tenantId, Guid.NewGuid(), "STUDIO-12", "A", 2, null, SomeAddress);
        var commandB = new CreatePropertyCommand(tenantId, Guid.NewGuid(), "studio-12", "B", 2, null, SomeAddress);

        var taskA = ExecuteAsync<CreatePropertyCommand, PropertyResult>(hostA, commandA, tenantId);
        var taskB = ExecuteAsync<CreatePropertyCommand, PropertyResult>(hostB, commandB, tenantId);
        var results = await Task.WhenAll(taskA, taskB);

        results.Count(r => r.IsSuccess).Should().Be(1);
        var winnerId = results.Single(r => r.IsSuccess).Value.Id;
        var failure = results.Single(r => r.IsFailure);
        failure.Error.Code.Should().Be(PropertyManagementErrorCodes.PropertyCodeAlreadyExists);

        // BarrierPropertyAuditWriter only synchronizes the two racing
        // transactions — it deliberately never stages an audit entry (see
        // its own declaration), so only the winner's Property row itself is
        // asserted here, mirroring CondominiumCommandHandlerTests' equivalent
        // barrier test, which likewise never re-queries the audit log
        // afterward.
        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);
        (await dbContext.Properties.CountAsync(p => p.Id == winnerId)).Should().Be(1, "the winning transaction's Property row should have committed");
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
