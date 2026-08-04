using FluentAssertions;
using IHostPro.BuildingBlocks.Infrastructure.Messaging;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.Contexts.PropertyManagement.Domain;
using IHostPro.Contexts.PropertyManagement.Domain.ValueObjects;
using IHostPro.Contexts.PropertyManagement.Infrastructure.Persistence;
using JasperFx;
using JasperFx.Resources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Testcontainers.PostgreSql;
using Wolverine;
using Wolverine.EntityFrameworkCore;

namespace IHostPro.Contexts.PropertyManagement.Tests.Integration;

/// <summary>
/// Exercises the Property Management Bounded Context's physical foundation
/// against a real PostgreSQL instance (Testcontainers): migration
/// application/idempotency, Row-Level Security, the tenant-aware composite
/// foreign key, the normalized-code unique constraint, the effective-address
/// CHECK constraint, the audit log's append-only grant, and the messaging
/// schema's provisioning — Fase 2, Incremento 1, Checkpoint 1 plan, item 9.
/// Mirrors <c>IdentityRowLevelSecurityTests</c>/
/// <c>IdentityOutboxTransactionExecutorTests</c> exactly. As no Command
/// exists yet, tenant-owned rows are seeded directly through the migrator
/// connection (still going through set_config('app.tenant_id', ...) first,
/// since FORCE ROW LEVEL SECURITY applies even to the table owner) purely to
/// exercise constraints/RLS — never as a stand-in for a future Command.
/// </summary>
public class PropertyManagementFoundationTests : IClassFixture<PropertyManagementFoundationTests.Fixture>
{
    private const string MessagingSchema = "property_management_messaging";

    private readonly string _migratorConnectionString;
    private readonly string _appConnectionString;

    public PropertyManagementFoundationTests(Fixture fixture)
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

            await ProvisionMessagingSchemaAsMigratorAsync();
        }

        public async Task DisposeAsync() => await _container.DisposeAsync();

        private async Task ProvisionMessagingSchemaAsMigratorAsync()
        {
            var hostBuilder = Host.CreateApplicationBuilder();
            hostBuilder.UseWolverine(opts =>
            {
                opts.EnrollAncillaryPostgresqlOutbox(MigratorConnectionString, MessagingSchema, typeof(PropertyManagementDbContext));
                opts.AutoBuildMessageStorageOnStartup = AutoCreate.None;
                opts.UseEntityFrameworkCoreTransactions();
            });

            using (var outboxHost = hostBuilder.Build())
            {
                await outboxHost.SetupResources();
            }

            await using var connection = new NpgsqlConnection(MigratorConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                GRANT USAGE ON SCHEMA {MessagingSchema} TO ihostpro_app;
                GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA {MessagingSchema} TO ihostpro_app;
                GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA {MessagingSchema} TO ihostpro_app;
                ALTER DEFAULT PRIVILEGES FOR ROLE ihostpro_migrator IN SCHEMA {MessagingSchema}
                  GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO ihostpro_app;
                ALTER DEFAULT PRIVILEGES FOR ROLE ihostpro_migrator IN SCHEMA {MessagingSchema}
                  GRANT USAGE, SELECT ON SEQUENCES TO ihostpro_app;
                """;
            await command.ExecuteNonQueryAsync();
        }
    }

    // ---- Migration ----

    [Fact]
    public async Task Migration_applies_cleanly_and_creates_the_expected_tables()
    {
        await using var connection = new NpgsqlConnection(_migratorConnectionString);
        await connection.OpenAsync();

        var tableNames = new HashSet<string>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "SELECT table_name FROM information_schema.tables WHERE table_schema = 'property_management'";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                tableNames.Add(reader.GetString(0));
        }

        tableNames.Should().Contain(["condominiums", "properties", "property_owners", "property_audit_log"]);
    }

    [Fact]
    public async Task Migration_is_idempotent_on_a_second_run()
    {
        await using var dbContext = CreateDbContext(_migratorConnectionString, new TenantContext());

        var act = async () => await dbContext.Database.MigrateAsync();

        await act.Should().NotThrowAsync();
    }

    // ---- Row-Level Security ----

    [Fact]
    public async Task Correct_tenant_sees_its_own_condominium()
    {
        var (tenantId, condominiumId) = await SeedCondominiumAsync();

        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);
        await using var dbContext = CreateDbContext(_appConnectionString, tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        var condominiums = await dbContext.Condominiums.ToListAsync();

        condominiums.Should().ContainSingle(c => c.Id == condominiumId);
    }

    [Fact]
    public async Task Wrong_tenant_sees_zero_rows()
    {
        var (_, condominiumId) = await SeedCondominiumAsync();
        var (unrelatedTenantId, _) = await SeedCondominiumAsync();

        var tenantContext = new TenantContext();
        tenantContext.SetTenant(unrelatedTenantId);
        await using var dbContext = CreateDbContext(_appConnectionString, tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, unrelatedTenantId);

        var visible = await dbContext.Condominiums.Where(c => c.Id == condominiumId).ToListAsync();

        visible.Should().BeEmpty();
    }

    [Fact]
    public async Task Absent_tenant_context_sees_zero_rows_and_does_not_throw()
    {
        await SeedCondominiumAsync();

        await using var connection = new NpgsqlConnection(_appConnectionString);
        await connection.OpenAsync();

        var count = (long)(await ExecuteScalarAsync(connection, "SELECT count(*) FROM property_management.condominiums"))!;

        count.Should().Be(0);
    }

    [Fact]
    public async Task Insert_without_tenant_context_fails_closed()
    {
        await using var dbContext = CreateDbContext(_migratorConnectionString, new TenantContext());
        await using var transaction = await dbContext.Database.BeginTransactionAsync();

        var condominium = Condominium.Create(
            Guid.NewGuid(), Guid.NewGuid(), "Test Condominium", SomeAddress(), DateTimeOffset.UtcNow);
        dbContext.Condominiums.Add(condominium);

        // app.tenant_id was never set on this transaction.
        var act = async () => await dbContext.SaveChangesAsync();

        await act.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task App_role_cannot_create_alter_or_drop_tables()
    {
        await using var connection = new NpgsqlConnection(_appConnectionString);
        await connection.OpenAsync();

        var createAct = async () => await ExecuteAsync(
            connection, "CREATE TABLE property_management.hack (id uuid PRIMARY KEY)");
        await createAct.Should().ThrowAsync<PostgresException>();

        var alterAct = async () => await ExecuteAsync(
            connection, "ALTER TABLE property_management.properties ADD COLUMN hack text");
        await alterAct.Should().ThrowAsync<PostgresException>();

        var dropAct = async () => await ExecuteAsync(connection, "DROP TABLE property_management.properties");
        await dropAct.Should().ThrowAsync<PostgresException>();
    }

    [Fact]
    public async Task App_role_cannot_disable_row_level_security_on_a_table()
    {
        await using var connection = new NpgsqlConnection(_appConnectionString);
        await connection.OpenAsync();

        var act = async () => await ExecuteAsync(
            connection, "ALTER TABLE property_management.properties DISABLE ROW LEVEL SECURITY");

        await act.Should().ThrowAsync<PostgresException>();
    }

    // ---- Composite foreign key / cross-tenant association ----

    [Fact]
    public async Task Composite_foreign_key_prevents_associating_a_property_with_another_tenants_condominium()
    {
        var (tenantAId, _) = await SeedCondominiumAsync();
        var (tenantBId, condominiumBId) = await SeedCondominiumAsync();

        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantAId);
        await using var dbContext = CreateDbContext(_migratorConnectionString, tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantAId);

        // condominiumBId belongs to tenant B, but this Property is being
        // inserted under tenant A's session — the composite FK
        // (tenant_id, condominium_id) -> condominiums(tenant_id, id) must
        // reject it, since no row (tenantAId, condominiumBId) exists.
        var property = Property.Create(
            Guid.NewGuid(), tenantAId, PropertyCode.Create("X1"), "Cross-tenant attempt", 2,
            condominiumBId, address: null, DateTimeOffset.UtcNow);
        dbContext.Properties.Add(property);

        var act = async () => await dbContext.SaveChangesAsync();

        await act.Should().ThrowAsync<DbUpdateException>();
    }

    // ---- Unique normalized code ----

    [Fact]
    public async Task Unique_normalized_code_constraint_is_case_insensitive()
    {
        var tenantId = Guid.NewGuid();

        await SeedPropertyAsync(tenantId, "A1", condominiumId: null, address: SomeAddress());

        var act = async () => await SeedPropertyAsync(tenantId, "a1", condominiumId: null, address: SomeAddress());

        await act.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task Same_code_is_allowed_across_different_tenants()
    {
        await SeedPropertyAsync(Guid.NewGuid(), "A1", condominiumId: null, address: SomeAddress());

        var act = async () => await SeedPropertyAsync(Guid.NewGuid(), "A1", condominiumId: null, address: SomeAddress());

        await act.Should().NotThrowAsync();
    }

    // ---- Effective-address CHECK constraint ----

    [Fact]
    public async Task Check_constraint_allows_a_property_with_a_condominium_and_no_own_address()
    {
        var (tenantId, condominiumId) = await SeedCondominiumAsync();

        var act = async () => await SeedPropertyAsync(tenantId, "A1", condominiumId, address: null);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Check_constraint_allows_a_property_with_its_own_address_and_no_condominium()
    {
        var act = async () => await SeedPropertyAsync(Guid.NewGuid(), "A1", condominiumId: null, address: SomeAddress());

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Check_constraint_rejects_a_property_with_neither_condominium_nor_own_address()
    {
        // Bypasses the Property.Create() in-process invariant (Checkpoint 0
        // plan, item 5) via a raw INSERT, to prove the CHECK constraint
        // itself — not just the domain constructor — actually protects the
        // database against a concurrent write that skips the aggregate.
        var tenantId = Guid.NewGuid();
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);

        await using var connection = new NpgsqlConnection(_migratorConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await ExecuteAsync(connection, $"SET LOCAL app.tenant_id = '{tenantId}'");

        var act = async () => await ExecuteAsync(connection, $"""
            INSERT INTO property_management.properties
                (id, tenant_id, code, normalized_code, name, capacity, condominium_id, status, created_at, updated_at, xmin)
            VALUES
                ('{Guid.NewGuid()}', '{tenantId}', 'A1', 'A1', 'No Address', 2, NULL, 'Draft', now(), now(), 0);
            """);

        await act.Should().ThrowAsync<PostgresException>();
    }

    // ---- Audit log: append-only ----

    [Fact]
    public async Task Audit_log_accepts_insert_through_the_app_role()
    {
        var tenantId = Guid.NewGuid();
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);

        await using var dbContext = CreateDbContext(_appConnectionString, tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        var entry = PropertyAuditEntry.Create(
            Guid.NewGuid(), tenantId, Guid.NewGuid(), "Property", Guid.NewGuid(), "property_created",
            ["name", "capacity"], DateTimeOffset.UtcNow);
        dbContext.PropertyAuditLog.Add(entry);

        var act = async () => await dbContext.SaveChangesAsync();

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task App_role_cannot_update_audit_log_rows()
    {
        var (tenantId, entryId) = await SeedAuditEntryAsync();

        await using var connection = new NpgsqlConnection(_appConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await ExecuteAsync(connection, $"SET LOCAL app.tenant_id = '{tenantId}'");

        var act = async () => await ExecuteAsync(
            connection, $"UPDATE property_management.property_audit_log SET action_code = 'tampered' WHERE id = '{entryId}'");

        await act.Should().ThrowAsync<PostgresException>();
    }

    [Fact]
    public async Task App_role_cannot_delete_audit_log_rows()
    {
        var (tenantId, entryId) = await SeedAuditEntryAsync();

        await using var connection = new NpgsqlConnection(_appConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await ExecuteAsync(connection, $"SET LOCAL app.tenant_id = '{tenantId}'");

        var act = async () => await ExecuteAsync(
            connection, $"DELETE FROM property_management.property_audit_log WHERE id = '{entryId}'");

        await act.Should().ThrowAsync<PostgresException>();
    }

    // ---- Messaging schema provisioning ----

    [Fact]
    public async Task Messaging_schema_is_provisioned_and_readable_by_the_app_role()
    {
        await using var connection = new NpgsqlConnection(_appConnectionString);
        await connection.OpenAsync();

        var tableCount = (long)(await ExecuteScalarAsync(
            connection,
            $"SELECT count(*) FROM information_schema.tables WHERE table_schema = '{MessagingSchema}'"))!;

        tableCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task App_role_has_no_ddl_privileges_on_the_messaging_schema()
    {
        await using var connection = new NpgsqlConnection(_appConnectionString);
        await connection.OpenAsync();

        var act = async () => await ExecuteAsync(
            connection, $"CREATE TABLE {MessagingSchema}.hack (id uuid PRIMARY KEY)");

        await act.Should().ThrowAsync<PostgresException>();
    }

    // ---- Helpers ----

    private static Address SomeAddress() => Address.Create(
        "01310100", "Av. Paulista", "1000", null, "Bela Vista", "São Paulo", "SP");

    private async Task<(Guid TenantId, Guid CondominiumId)> SeedCondominiumAsync()
    {
        var tenantId = Guid.NewGuid();
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);

        await using var dbContext = CreateDbContext(_migratorConnectionString, tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        var condominium = Condominium.Create(
            Guid.NewGuid(), tenantId, "Test Condominium", SomeAddress(), DateTimeOffset.UtcNow);
        dbContext.Condominiums.Add(condominium);

        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();

        return (tenantId, condominium.Id);
    }

    private async Task SeedPropertyAsync(Guid tenantId, string code, Guid? condominiumId, Address? address)
    {
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);

        await using var dbContext = CreateDbContext(_migratorConnectionString, tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        var property = Property.Create(
            Guid.NewGuid(), tenantId, PropertyCode.Create(code), "Test Property", 2, condominiumId, address,
            DateTimeOffset.UtcNow);
        dbContext.Properties.Add(property);

        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();
    }

    private async Task<(Guid TenantId, Guid EntryId)> SeedAuditEntryAsync()
    {
        var tenantId = Guid.NewGuid();
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);

        await using var dbContext = CreateDbContext(_migratorConnectionString, tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        var entry = PropertyAuditEntry.Create(
            Guid.NewGuid(), tenantId, Guid.NewGuid(), "Property", Guid.NewGuid(), "property_created",
            ["name"], DateTimeOffset.UtcNow);
        dbContext.PropertyAuditLog.Add(entry);

        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();

        return (tenantId, entry.Id);
    }

    private static async Task SetTenantAsync(PropertyManagementDbContext dbContext, Guid tenantId) =>
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.tenant_id', {tenantId.ToString()}, true)");

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

    private static async Task<object?> ExecuteScalarAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }
}
