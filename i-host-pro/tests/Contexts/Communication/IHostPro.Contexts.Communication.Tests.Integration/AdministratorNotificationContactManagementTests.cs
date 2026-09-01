using FluentAssertions;
using IHostPro.BuildingBlocks.Infrastructure.Messaging;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.Contexts.Communication.Application;
using IHostPro.Contexts.Communication.Domain;
using IHostPro.Contexts.Communication.Infrastructure;
using IHostPro.Contexts.Communication.Infrastructure.Persistence;
using JasperFx;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Wolverine;
using Wolverine.EntityFrameworkCore;

namespace IHostPro.Contexts.Communication.Tests.Integration;

/// <summary>
/// Fase 11, Checkpoint 6 — Get/UpsertAdministratorNotificationContact through
/// the real dispatcher, plus direct schema verification (RLS, partial unique
/// "at most one active per Tenant" index, cross-tenant isolation). Mirrors
/// <c>SendHumanHandoffNotificationCommandHandlerTests</c>' own composition
/// root exactly.
/// </summary>
public class AdministratorNotificationContactManagementTests : IClassFixture<CommunicationMessageExecutionScopeTests.Fixture>
{
    private readonly CommunicationMessageExecutionScopeTests.Fixture _fixture;

    public AdministratorNotificationContactManagementTests(CommunicationMessageExecutionScopeTests.Fixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Upsert_creates_the_first_contact_then_Get_returns_it()
    {
        var tenantId = Guid.NewGuid();
        using var host = BuildHost();

        var upserted = await UpsertAsync(host, tenantId, "+5511977776666");
        upserted.IsFailure.Should().BeFalse();
        upserted.Value.DestinationPhone.Should().Be("+5511977776666");
        upserted.Value.IsActive.Should().BeTrue();

        var fetched = await GetAsync(host, tenantId);
        fetched.IsFailure.Should().BeFalse();
        fetched.Value.Should().NotBeNull();
        fetched.Value!.DestinationPhone.Should().Be("+5511977776666");
    }

    [Fact]
    public async Task Upsert_called_twice_replaces_the_phone_never_creating_a_second_row()
    {
        var tenantId = Guid.NewGuid();
        using var host = BuildHost();

        var first = await UpsertAsync(host, tenantId, "+5511977776666");
        var second = await UpsertAsync(host, tenantId, "+5511988885555");

        second.Value.Id.Should().Be(first.Value.Id, "the same Tenant's active contact is replaced in place, never duplicated");
        second.Value.DestinationPhone.Should().Be("+5511988885555");

        (await CountContactsAsync(tenantId)).Should().Be(1);
    }

    [Fact]
    public async Task Get_returns_null_when_no_contact_exists_yet()
    {
        var tenantId = Guid.NewGuid();
        using var host = BuildHost();

        var result = await GetAsync(host, tenantId);

        result.IsFailure.Should().BeFalse();
        result.Value.Should().BeNull();
    }

    [Fact]
    public async Task Cross_tenant_contacts_are_never_visible_to_each_other()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        using var host = BuildHost();

        await UpsertAsync(host, tenantA, "+5511977776666");

        var resultForB = await GetAsync(host, tenantB);

        resultForB.Value.Should().BeNull("Tenant B must never see Tenant A's own administrator contact");
    }

    [Fact]
    public async Task Schema_enforces_at_most_one_active_contact_per_tenant_via_partial_unique_index()
    {
        var tenantId = Guid.NewGuid();
        await using var connection = new NpgsqlConnection(_fixture.MigratorConnectionString);
        await connection.OpenAsync();

        await using (var setCommand = connection.CreateCommand())
        {
            setCommand.CommandText = $"SET LOCAL app.tenant_id = '{tenantId:D}'";
            await setCommand.ExecuteNonQueryAsync();
        }

        await using var transaction = await connection.BeginTransactionAsync();
        await using (var setCommand = connection.CreateCommand())
        {
            setCommand.Transaction = transaction;
            setCommand.CommandText = $"SET LOCAL app.tenant_id = '{tenantId:D}'";
            await setCommand.ExecuteNonQueryAsync();
        }

        await using (var insertCommand = connection.CreateCommand())
        {
            insertCommand.Transaction = transaction;
            insertCommand.CommandText =
                "INSERT INTO communication.administrator_notification_contacts (id, tenant_id, destination_phone, is_active, created_at_utc, updated_at_utc) " +
                "VALUES (@id, @tenantId, @phone, true, now(), now())";
            insertCommand.Parameters.AddWithValue("id", Guid.NewGuid());
            insertCommand.Parameters.AddWithValue("tenantId", tenantId);
            insertCommand.Parameters.AddWithValue("phone", "+5511977776666");
            await insertCommand.ExecuteNonQueryAsync();
        }

        Func<Task> secondInsert = async () =>
        {
            await using var insertCommand = connection.CreateCommand();
            insertCommand.Transaction = transaction;
            insertCommand.CommandText =
                "INSERT INTO communication.administrator_notification_contacts (id, tenant_id, destination_phone, is_active, created_at_utc, updated_at_utc) " +
                "VALUES (@id, @tenantId, @phone, true, now(), now())";
            insertCommand.Parameters.AddWithValue("id", Guid.NewGuid());
            insertCommand.Parameters.AddWithValue("tenantId", tenantId);
            insertCommand.Parameters.AddWithValue("phone", "+5511988885555");
            await insertCommand.ExecuteNonQueryAsync();
        };

        await secondInsert.Should().ThrowAsync<PostgresException>("a second ACTIVE contact for the same Tenant must violate the partial unique index");

        await transaction.RollbackAsync();
    }

    // ---- Composition root -------------------------------------------------

    private IHost BuildHost()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Communication"] = _fixture.AppConnectionString })
            .Build();

        var hostBuilder = Host.CreateApplicationBuilder();
        hostBuilder.Services.AddScoped<ITenantContext, TenantContext>();
        hostBuilder.Services.AddLogging();
        hostBuilder.Services.AddCommunicationModule(configuration);

        hostBuilder.UseWolverine(opts =>
        {
            opts.EnrollAncillaryPostgresqlOutbox(_fixture.AppConnectionString, "communication_messaging", typeof(CommunicationDbContext));
            opts.AutoBuildMessageStorageOnStartup = AutoCreate.None;
            opts.UseEntityFrameworkCoreTransactions();
        });

        return hostBuilder.Build();
    }

    private static async Task<IHostPro.BuildingBlocks.Domain.Result<AdministratorNotificationContactResult>> UpsertAsync(IHost host, Guid tenantId, string phone)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var tenantContext = scope.ServiceProvider.GetRequiredService<ITenantContext>();
        tenantContext.SetTenant(tenantId);
        var dispatcher = scope.ServiceProvider.GetRequiredService<ICommunicationRequestDispatcher>();

        return await dispatcher.Send(new UpsertAdministratorNotificationContactCommand { TenantId = tenantId, DestinationPhone = phone });
    }

    private static async Task<IHostPro.BuildingBlocks.Domain.Result<AdministratorNotificationContactResult?>> GetAsync(IHost host, Guid tenantId)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var tenantContext = scope.ServiceProvider.GetRequiredService<ITenantContext>();
        tenantContext.SetTenant(tenantId);
        var dispatcher = scope.ServiceProvider.GetRequiredService<ICommunicationRequestDispatcher>();

        return await dispatcher.Send(new GetAdministratorNotificationContactQuery { TenantId = tenantId });
    }

    private async Task<int> CountContactsAsync(Guid tenantId)
    {
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);
        var options = new DbContextOptionsBuilder<CommunicationDbContext>()
            .UseNpgsql(_fixture.MigratorConnectionString, npgsqlOptions => npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "communication"))
            .Options;
        await using var dbContext = new CommunicationDbContext(options, tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await dbContext.Database.ExecuteSqlInterpolatedAsync($"SELECT set_config('app.tenant_id', {tenantId.ToString()}, true)");

        return await dbContext.AdministratorNotificationContacts.CountAsync(c => c.TenantId == tenantId);
    }
}
