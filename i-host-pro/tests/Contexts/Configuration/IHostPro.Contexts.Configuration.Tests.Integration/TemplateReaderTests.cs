using FluentAssertions;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.Contexts.Configuration.Contracts;
using IHostPro.Contexts.Configuration.Domain;
using IHostPro.Contexts.Configuration.Infrastructure;
using IHostPro.Contexts.Configuration.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;

namespace IHostPro.Contexts.Configuration.Tests.Integration;

/// <summary>
/// Exercises <see cref="ITemplateReader"/> — the general Configuration &amp;
/// Policy synchronous-read exception (ADR-002), reused as-is for Templates
/// (Fase 9, Checkpoint 1) — against a real PostgreSQL instance
/// (Testcontainers). Mirrors <c>PolicyResolutionTests</c>'s own composition-
/// root/seeding structure.
/// </summary>
public class TemplateReaderTests : IClassFixture<TemplateReaderTests.Fixture>
{
    private readonly Fixture _fixture;

    public TemplateReaderTests(Fixture fixture) => _fixture = fixture;

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
                    ["ConnectionStrings:Configuration"] = ConnectionString,
                    ["Configuration:PolicyCache:ConnectionString"] = "localhost:1",
                })
                .Build();

            services.AddScoped<ITenantContext, TenantContext>();
            services.AddLogging();
            services.AddConfigurationModule(configuration);
            _serviceProvider = services.BuildServiceProvider();

            await using var scope = _serviceProvider.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<ConfigurationDbContext>();
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
    public async Task GetActiveByKeyAsync_returns_null_when_no_template_exists()
    {
        var result = await ResolveAsync(Guid.NewGuid(), "MISSING");

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetActiveByKeyAsync_returns_the_active_template()
    {
        var tenantId = Guid.NewGuid();
        await SeedTemplateAsync(tenantId, "RESERVATION_CONFIRMATION", "Olá {{GuestName}}", isActive: true);

        var result = await ResolveAsync(tenantId, "RESERVATION_CONFIRMATION");

        result.Should().NotBeNull();
        result!.Key.Should().Be("RESERVATION_CONFIRMATION");
        result.Content.Should().Be("Olá {{GuestName}}");
    }

    [Fact]
    public async Task GetActiveByKeyAsync_never_resolves_an_inactive_template()
    {
        var tenantId = Guid.NewGuid();
        await SeedTemplateAsync(tenantId, "RESERVATION_CONFIRMATION", "Olá {{GuestName}}", isActive: false);

        var result = await ResolveAsync(tenantId, "RESERVATION_CONFIRMATION");

        result.Should().BeNull("an explicitly deactivated Template must never be resolved as the active one");
    }

    [Fact]
    public async Task GetActiveByKeyAsync_never_resolves_a_template_belonging_to_another_tenant()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        await SeedTemplateAsync(tenantA, "RESERVATION_CONFIRMATION", "Olá {{GuestName}}", isActive: true);

        var result = await ResolveAsync(tenantB, "RESERVATION_CONFIRMATION");

        result.Should().BeNull("RLS/the Global Query Filter must isolate templates per tenant");
    }

    // ---- Helpers ----

    /// <summary>
    /// In production, <c>TenantResolutionMiddleware</c> resolves the
    /// request's tenant onto the DI-scoped <see cref="ITenantContext"/>
    /// (shared by <c>ConfigurationDbContext</c>'s own Global Query Filter)
    /// before any reader runs. There is no middleware here, so this
    /// reproduces that one step itself — mirrors <c>PolicyResolutionTests</c>'
    /// own <c>SetAmbientTenant</c>: <see cref="TemplateReader"/>'s own
    /// throwaway <see cref="TenantContext"/> only ever drives the RLS
    /// session variable, never the Global Query Filter.
    /// </summary>
    private async Task<ActiveTemplate?> ResolveAsync(Guid tenantId, string key)
    {
        await using var scope = _fixture.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(tenantId);
        var reader = scope.ServiceProvider.GetRequiredService<ITemplateReader>();
        return await reader.GetActiveByKeyAsync(tenantId, key, CancellationToken.None);
    }

    private async Task SeedTemplateAsync(Guid tenantId, string key, string content, bool isActive)
    {
        await using var dbContext = CreateDbContext(_fixture.ConnectionString);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        var template = Template.Create(Guid.NewGuid(), tenantId, key, content, DateTimeOffset.UtcNow);
        if (!isActive)
            template.Deactivate(DateTimeOffset.UtcNow);
        dbContext.Templates.Add(template);

        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();
    }

    private static async Task SetTenantAsync(ConfigurationDbContext dbContext, Guid tenantId) =>
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.tenant_id', {tenantId.ToString()}, true)");

    private static ConfigurationDbContext CreateDbContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<ConfigurationDbContext>()
            .UseNpgsql(connectionString, npgsqlOptions =>
                npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "configuration"))
            .Options;

        return new ConfigurationDbContext(options, new TenantContext());
    }
}
