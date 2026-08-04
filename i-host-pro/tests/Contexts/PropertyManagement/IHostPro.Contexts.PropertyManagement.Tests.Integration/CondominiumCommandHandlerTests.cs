using FluentAssertions;
using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.BuildingBlocks.Infrastructure.Messaging;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.BuildingBlocks.Infrastructure.Persistence;
using IHostPro.Contexts.PropertyManagement.Application;
using IHostPro.Contexts.PropertyManagement.Application.Condominiums;
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
/// End-to-end test of the Create/UpdateCondominium use cases against a real
/// PostgreSQL instance (Fase 2, Incremento 1, Checkpoint 2 plan, item 16) —
/// mirrors <c>ChangePasswordCommandHandlerTests</c>'s structure, but dispatches
/// through the REAL production composition root
/// (<c>AddPropertyManagementModule</c>/<c>AddPropertyManagementCommandDispatch</c>
/// via <see cref="ISender"/>) rather than hand-wiring individual services —
/// "usar composição real do Host... quando for possível reutilizar o
/// composition root oficial" (Checkpoint 2 plan, item 17).
/// </summary>
public class CondominiumCommandHandlerTests : IClassFixture<CondominiumCommandHandlerTests.Fixture>
{
    private const string OutboxSchema = "property_management_messaging";

    private readonly string _migratorConnectionString;
    private readonly string _appConnectionString;

    public CondominiumCommandHandlerTests(Fixture fixture)
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

    private static readonly CondominiumAddressInput SomeAddress = new(
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

    private static async Task<Result<CondominiumResult>> ExecuteAsync<TMessage>(IHost host, TMessage message, Guid tenantId)
        where TMessage : IRequest<Result<CondominiumResult>>
    {
        using var scope = host.Services.CreateScope();
        var sp = scope.ServiceProvider;
        sp.GetRequiredService<ITenantContext>().SetTenant(tenantId);

        return await sp.GetRequiredService<ISender>().Send(message, CancellationToken.None);
    }

    // ---- Tests: Create happy path / same-commit guarantees ----------------

    /// <summary>
    /// Envelope-in-the-same-commit is verified separately in
    /// <c>CondominiumIntegrationEventsTests</c> (Postgres+RabbitMQ, with a
    /// real route registered for <c>CondominiumCreated</c>/<c>CondominiumUpdated</c>)
    /// — a route-less host here would make <c>IDbContextOutbox.PublishAsync</c>
    /// a no-op, so asserting envelope presence against THIS host would be
    /// meaningless. Here we only assert the aggregate + audit entry, mirroring
    /// <c>ChangePasswordCommandHandlerTests</c>'s own scope split from
    /// <c>IdentityIntegrationEventsTests</c>.
    /// </summary>
    [Fact]
    public async Task Creating_a_condominium_persists_it_and_exactly_one_audit_entry_in_the_same_commit()
    {
        var tenantId = Guid.NewGuid();
        using var host = await BuildHostAsync();

        var result = await ExecuteAsync(host, new CreateCondominiumCommand(tenantId, Guid.NewGuid(), "Condomínio Exemplo", SomeAddress), tenantId);

        result.IsSuccess.Should().BeTrue();

        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);

        (await dbContext.Condominiums.CountAsync(c => c.Id == result.Value.Id)).Should().Be(1);
        (await dbContext.PropertyAuditLog.CountAsync(e => e.AggregateId == result.Value.Id)).Should().Be(1);
    }

    [Fact]
    public async Task Updating_a_condominium_persists_the_change_and_exactly_one_audit_entry_in_the_same_commit()
    {
        var tenantId = Guid.NewGuid();
        using var host = await BuildHostAsync();
        var created = await ExecuteAsync(host, new CreateCondominiumCommand(tenantId, Guid.NewGuid(), "Original", SomeAddress), tenantId);

        var result = await ExecuteAsync(
            host, new UpdateCondominiumCommand(tenantId, Guid.NewGuid(), created.Value.Id, "Updated", null), tenantId);

        result.IsSuccess.Should().BeTrue();

        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);

        (await dbContext.Condominiums.Where(c => c.Id == created.Value.Id).Select(c => c.Name).SingleAsync()).Should().Be("Updated");
        (await dbContext.PropertyAuditLog.CountAsync(e => e.AggregateId == created.Value.Id && e.ActionCode == "condominium_updated")).Should().Be(1);
    }

    [Fact]
    public async Task A_no_op_update_persists_no_audit_entry_and_no_outbox_envelope()
    {
        var tenantId = Guid.NewGuid();
        using var host = await BuildHostAsync();
        var created = await ExecuteAsync(host, new CreateCondominiumCommand(tenantId, Guid.NewGuid(), "Same Name", SomeAddress), tenantId);

        var result = await ExecuteAsync(
            host, new UpdateCondominiumCommand(tenantId, Guid.NewGuid(), created.Value.Id, "Same Name", null), tenantId);

        result.IsSuccess.Should().BeTrue();

        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);

        (await dbContext.PropertyAuditLog.CountAsync(e => e.AggregateId == created.Value.Id && e.ActionCode == "condominium_updated")).Should().Be(0);
        (await CountEnvelopesByMessageTypeAsync("IHostPro.Contexts.PropertyManagement.Contracts.CondominiumUpdated")).Should().Be(0);
    }

    // ---- Tests: RLS / tenant isolation --------------------------------------

    [Fact]
    public async Task A_condominium_is_invisible_to_a_different_tenant()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        using var host = await BuildHostAsync();
        var created = await ExecuteAsync(host, new CreateCondominiumCommand(tenantA, Guid.NewGuid(), "Tenant A's", SomeAddress), tenantA);

        var result = await ExecuteAsync(host, new GetCondominiumDetailQuery(created.Value.Id), tenantB);

        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task Absent_tenant_context_fails_closed()
    {
        using var host = await BuildHostAsync();
        using var scope = host.Services.CreateScope();

        // ITenantContext.SetTenant is never called on this scope.
        var act = async () => await scope.ServiceProvider.GetRequiredService<ISender>()
            .Send(new CreateCondominiumCommand(Guid.NewGuid(), Guid.NewGuid(), "X", SomeAddress), CancellationToken.None);

        await act.Should().ThrowAsync<TenantContextNotResolvedException>();
    }

    // ---- Tests: rollback -----------------------------------------------------

    private sealed class ThrowingPropertyAuditWriter : IPropertyAuditWriter
    {
        public void Record(PropertyAuditEntry entry) =>
            throw new InvalidOperationException("Simulated failure after Condominium.Create() was staged.");
    }

    [Fact]
    public async Task A_failure_after_the_condominium_was_staged_rolls_back_the_entire_transaction_with_no_partial_state()
    {
        var tenantId = Guid.NewGuid();
        using var host = await BuildHostAsync(overrides: sc =>
            sc.AddScoped<IPropertyAuditWriter, ThrowingPropertyAuditWriter>());

        var act = async () => await ExecuteAsync(host, new CreateCondominiumCommand(tenantId, Guid.NewGuid(), "X", SomeAddress), tenantId);

        await act.Should().ThrowAsync<InvalidOperationException>();

        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);

        (await dbContext.Condominiums.CountAsync()).Should().Be(0);
        (await CountEnvelopesByMessageTypeAsync("IHostPro.Contexts.PropertyManagement.Contracts.CondominiumCreated")).Should().Be(0);
    }

    // ---- Tests: real concurrency ---------------------------------------------

    private sealed class BarrierPropertyAuditWriter : IPropertyAuditWriter
    {
        private readonly Barrier _barrier;

        public BarrierPropertyAuditWriter(Barrier barrier) => _barrier = barrier;

        public void Record(PropertyAuditEntry entry) => _barrier.SignalAndWait(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Two_concurrent_updates_of_the_same_condominium_allow_only_one_to_succeed_and_translate_to_CondominiumConcurrencyConflict_without_retry()
    {
        var tenantId = Guid.NewGuid();
        using var seedHost = await BuildHostAsync();
        var created = await ExecuteAsync(seedHost, new CreateCondominiumCommand(tenantId, Guid.NewGuid(), "Original", SomeAddress), tenantId);

        using var barrier = new Barrier(2);
        using var hostA = await BuildHostAsync(overrides: sc =>
            sc.AddScoped<IPropertyAuditWriter>(_ => new BarrierPropertyAuditWriter(barrier)));
        using var hostB = await BuildHostAsync(overrides: sc =>
            sc.AddScoped<IPropertyAuditWriter>(_ => new BarrierPropertyAuditWriter(barrier)));

        var taskA = ExecuteAsync(hostA, new UpdateCondominiumCommand(tenantId, Guid.NewGuid(), created.Value.Id, "Name A", null), tenantId);
        var taskB = ExecuteAsync(hostB, new UpdateCondominiumCommand(tenantId, Guid.NewGuid(), created.Value.Id, "Name B", null), tenantId);
        var results = await Task.WhenAll(taskA, taskB);

        results.Count(r => r.IsSuccess).Should().Be(1);
        var failure = results.Single(r => r.IsFailure);
        failure.Error.Code.Should().Be(
            IHostPro.Contexts.PropertyManagement.Application.Errors.PropertyManagementErrorCodes.CondominiumConcurrencyConflict);
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
