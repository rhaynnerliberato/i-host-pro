using FluentAssertions;
using IHostPro.BuildingBlocks.Infrastructure.Messaging;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.Contexts.Payments.Domain;
using IHostPro.Contexts.Payments.Infrastructure.Persistence;
using JasperFx;
using JasperFx.Resources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Testcontainers.PostgreSql;
using Wolverine;
using Wolverine.EntityFrameworkCore;

namespace IHostPro.Contexts.Payments.Tests.Integration;

/// <summary>
/// Exercises the Payments Bounded Context's physical foundation against a
/// real PostgreSQL instance (Testcontainers): migration application, Row-
/// Level Security, the active-charge partial unique index, and the
/// messaging schema's provisioning (Fase 10, Checkpoint 5 — PIX/Payment
/// Deterministic Foundation). Mirrors <c>PropertyManagementFoundationTests</c>
/// exactly.
/// </summary>
public class PaymentsFoundationTests : IClassFixture<PaymentsFoundationTests.Fixture>
{
    private const string MessagingSchema = "payments_messaging";

    private readonly string _migratorConnectionString;
    private readonly string _appConnectionString;

    public PaymentsFoundationTests(Fixture fixture)
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
                opts.EnrollAncillaryPostgresqlOutbox(MigratorConnectionString, MessagingSchema, typeof(PaymentsDbContext));
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

        private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }
    }

    // ---- Migration ----

    [Fact]
    public async Task Migration_applies_cleanly_and_creates_the_expected_table()
    {
        await using var dbContext = CreateDbContext(_migratorConnectionString, new TenantContext());

        var tableExists = await TableExistsAsync(dbContext, "payments", "pix_charges");

        tableExists.Should().BeTrue();
    }

    [Fact]
    public async Task Migration_is_idempotent_on_reapplication()
    {
        await using var dbContext = CreateDbContext(_migratorConnectionString, new TenantContext());

        var act = async () => await dbContext.Database.MigrateAsync();

        await act.Should().NotThrowAsync();
    }

    // ---- Row-Level Security ----

    [Fact]
    public async Task App_role_sees_only_its_own_tenant_rows()
    {
        var (tenantId, chargeId) = await SeedPixChargeAsync();

        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);
        await using var dbContext = CreateDbContext(_appConnectionString, tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        var charges = await dbContext.PixCharges.Where(c => c.Id == chargeId).ToListAsync();

        charges.Should().ContainSingle();
    }

    [Fact]
    public async Task Wrong_tenant_sees_zero_rows()
    {
        var (_, chargeId) = await SeedPixChargeAsync();
        var unrelatedTenantId = Guid.NewGuid();

        var tenantContext = new TenantContext();
        tenantContext.SetTenant(unrelatedTenantId);
        await using var dbContext = CreateDbContext(_appConnectionString, tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, unrelatedTenantId);

        var visible = await dbContext.PixCharges.Where(c => c.Id == chargeId).ToListAsync();

        visible.Should().BeEmpty();
    }

    [Fact]
    public async Task Absent_tenant_setting_fails_closed_to_zero_rows_even_for_the_migrator_role()
    {
        await SeedPixChargeAsync();

        await using var dbContext = CreateDbContext(_migratorConnectionString, new TenantContext());
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        // Deliberately no set_config('app.tenant_id', ...) call — RLS must fail closed.

        var visible = await dbContext.PixCharges.IgnoreQueryFilters().ToListAsync();

        visible.Should().BeEmpty();
    }

    // ---- Active-charge cardinality (mandate item 14) ----

    [Fact]
    public async Task A_second_Pending_charge_for_the_same_LateCheckoutRequestId_is_rejected()
    {
        var tenantId = Guid.NewGuid();
        var lateCheckoutRequestId = Guid.NewGuid();
        await SeedPixChargeAsync(tenantId, lateCheckoutRequestId);

        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);
        await using var dbContext = CreateDbContext(_migratorConnectionString, tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        var secondCharge = PixCharge.Create(Guid.NewGuid(), tenantId, lateCheckoutRequestId, Guid.NewGuid(), 50m, "BRL", DateTimeOffset.UtcNow);
        dbContext.PixCharges.Add(secondCharge);

        var act = async () => await dbContext.SaveChangesAsync();

        await act.Should().ThrowAsync<DbUpdateException>("only one ACTIVE (Pending) charge may exist per LateCheckoutRequestId");
    }

    [Fact]
    public async Task A_second_charge_is_allowed_once_the_first_is_Failed()
    {
        var tenantId = Guid.NewGuid();
        var lateCheckoutRequestId = Guid.NewGuid();
        var (_, firstChargeId) = await SeedPixChargeAsync(tenantId, lateCheckoutRequestId);

        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);
        await using (var failDbContext = CreateDbContext(_migratorConnectionString, tenantContext))
        await using (var failTransaction = await failDbContext.Database.BeginTransactionAsync())
        {
            await SetTenantAsync(failDbContext, tenantId);
            var firstCharge = await failDbContext.PixCharges.FirstAsync(c => c.Id == firstChargeId);
            firstCharge.Fail(DateTimeOffset.UtcNow);
            await failDbContext.SaveChangesAsync();
            await failTransaction.CommitAsync();
        }

        await using var dbContext = CreateDbContext(_migratorConnectionString, tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        var secondCharge = PixCharge.Create(Guid.NewGuid(), tenantId, lateCheckoutRequestId, Guid.NewGuid(), 50m, "BRL", DateTimeOffset.UtcNow);
        dbContext.PixCharges.Add(secondCharge);

        var act = async () => await dbContext.SaveChangesAsync();

        await act.Should().NotThrowAsync();
    }

    // ---- Helpers ----

    private async Task<(Guid TenantId, Guid ChargeId)> SeedPixChargeAsync(Guid? tenantId = null, Guid? lateCheckoutRequestId = null)
    {
        var resolvedTenantId = tenantId ?? Guid.NewGuid();
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(resolvedTenantId);

        await using var dbContext = CreateDbContext(_migratorConnectionString, tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, resolvedTenantId);

        var charge = PixCharge.Create(
            Guid.NewGuid(), resolvedTenantId, lateCheckoutRequestId ?? Guid.NewGuid(), Guid.NewGuid(), 100m, "BRL", DateTimeOffset.UtcNow);
        dbContext.PixCharges.Add(charge);

        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();

        return (resolvedTenantId, charge.Id);
    }

    private static async Task SetTenantAsync(PaymentsDbContext dbContext, Guid tenantId) =>
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.tenant_id', {tenantId.ToString()}, true)");

    private static async Task<bool> TableExistsAsync(PaymentsDbContext dbContext, string schema, string table)
    {
        await using var command = dbContext.Database.GetDbConnection().CreateCommand();
        if (command.Connection!.State != System.Data.ConnectionState.Open)
            await dbContext.Database.OpenConnectionAsync();

        command.CommandText = "SELECT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = @schema AND table_name = @table)";
        var schemaParam = command.CreateParameter();
        schemaParam.ParameterName = "schema";
        schemaParam.Value = schema;
        command.Parameters.Add(schemaParam);
        var tableParam = command.CreateParameter();
        tableParam.ParameterName = "table";
        tableParam.Value = table;
        command.Parameters.Add(tableParam);

        var result = await command.ExecuteScalarAsync();
        return result is true;
    }

    private static PaymentsDbContext CreateDbContext(string connectionString, ITenantContext tenantContext)
    {
        var options = new DbContextOptionsBuilder<PaymentsDbContext>()
            .UseNpgsql(connectionString, npgsqlOptions =>
                npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "payments"))
            .Options;

        return new PaymentsDbContext(options, tenantContext);
    }
}
