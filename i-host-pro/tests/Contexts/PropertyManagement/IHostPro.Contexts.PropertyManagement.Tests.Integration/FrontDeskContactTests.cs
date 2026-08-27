using FluentAssertions;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.Contexts.PropertyManagement.Domain;
using IHostPro.Contexts.PropertyManagement.Domain.ValueObjects;
using IHostPro.Contexts.PropertyManagement.Infrastructure.Communication;
using IHostPro.Contexts.PropertyManagement.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace IHostPro.Contexts.PropertyManagement.Tests.Integration;

/// <summary>
/// Fase 10, Checkpoint 4 (Portaria Notification Foundation) mandate §35/§36
/// — proves, against a real PostgreSQL instance: the "one active contact per
/// Condominium" cardinality rule (a plain unique constraint, per the
/// mandate-approved design — no historical rows), tenant isolation/RLS on
/// <c>front_desk_contacts</c>, and the ADR-026 synchronous exception #9
/// reader (<see cref="FrontDeskContactReader"/>) end to end: active contact
/// found, no contact configured, inactive contact treated as not-configured,
/// Property without a Condominium, tenant isolation, and the minimal
/// response shape (never the aggregate). Reuses
/// <see cref="PropertyManagementFoundationTests.Fixture"/> — same migrated
/// database, same roles.
/// </summary>
public class FrontDeskContactTests : IClassFixture<PropertyManagementFoundationTests.Fixture>
{
    private readonly string _migratorConnectionString;
    private readonly string _appConnectionString;

    public FrontDeskContactTests(PropertyManagementFoundationTests.Fixture fixture)
    {
        _migratorConnectionString = fixture.MigratorConnectionString;
        _appConnectionString = fixture.AppConnectionString;
    }

    // ---- Cardinality / unique constraint ----

    [Fact]
    public async Task Unique_constraint_rejects_a_second_contact_for_the_same_condominium()
    {
        var (tenantId, condominiumId) = await SeedCondominiumAsync();
        await SeedFrontDeskContactAsync(tenantId, condominiumId, "Portaria Bloco A", "+5511977776666", isActive: true);

        var act = async () => await SeedFrontDeskContactAsync(tenantId, condominiumId, "Portaria Bloco B", "+5511988885555", isActive: true);

        await act.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task Same_condominium_can_be_updated_in_place_via_UpdateContact()
    {
        var (tenantId, condominiumId) = await SeedCondominiumAsync();
        var contactId = await SeedFrontDeskContactAsync(tenantId, condominiumId, "Portaria Bloco A", "+5511977776666", isActive: true);

        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);
        await using var dbContext = CreateDbContext(_migratorConnectionString, tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        var contact = await dbContext.FrontDeskContacts.FirstAsync(c => c.Id == contactId);
        contact.UpdateContact("Portaria Bloco A (renovada)", "+5511988885555", isActive: false, DateTimeOffset.UtcNow);

        var act = async () => await dbContext.SaveChangesAsync();

        await act.Should().NotThrowAsync("updating the SAME row in place is always allowed — only a second distinct row is rejected");
    }

    // ---- Row-Level Security ----

    [Fact]
    public async Task Correct_tenant_sees_its_own_front_desk_contact()
    {
        var (tenantId, condominiumId) = await SeedCondominiumAsync();
        await SeedFrontDeskContactAsync(tenantId, condominiumId, "Portaria Bloco A", "+5511977776666", isActive: true);

        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);
        await using var dbContext = CreateDbContext(_appConnectionString, tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        var contacts = await dbContext.FrontDeskContacts.Where(c => c.CondominiumId == condominiumId).ToListAsync();

        contacts.Should().ContainSingle();
    }

    [Fact]
    public async Task Wrong_tenant_sees_zero_rows()
    {
        var (tenantId, condominiumId) = await SeedCondominiumAsync();
        await SeedFrontDeskContactAsync(tenantId, condominiumId, "irrelevant", "+5511900000000", true);

        var (unrelatedTenantId, _) = await SeedCondominiumAsync();
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(unrelatedTenantId);
        await using var dbContext = CreateDbContext(_appConnectionString, tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, unrelatedTenantId);

        var visible = await dbContext.FrontDeskContacts.Where(c => c.CondominiumId == condominiumId).ToListAsync();

        visible.Should().BeEmpty();
    }

    // ---- ADR-026 synchronous exception #9 reader ----

    [Fact]
    public async Task Reader_finds_the_active_contact_by_PropertyId()
    {
        var (tenantId, condominiumId) = await SeedCondominiumAsync();
        var propertyId = await SeedPropertyAsync(tenantId, condominiumId, "A1");
        await SeedFrontDeskContactAsync(tenantId, condominiumId, "Portaria Bloco A", "+5511977776666", isActive: true);

        var result = await ResolveAsync(tenantId, propertyId);

        result.Should().NotBeNull();
        result!.DisplayName.Should().Be("Portaria Bloco A");
        result.PhoneNumber.Should().Be("+5511977776666");
    }

    [Fact]
    public async Task Reader_returns_null_when_no_contact_is_configured()
    {
        var (tenantId, condominiumId) = await SeedCondominiumAsync();
        var propertyId = await SeedPropertyAsync(tenantId, condominiumId, "A1");

        var result = await ResolveAsync(tenantId, propertyId);

        result.Should().BeNull();
    }

    [Fact]
    public async Task Reader_treats_an_inactive_contact_the_same_as_not_configured()
    {
        var (tenantId, condominiumId) = await SeedCondominiumAsync();
        var propertyId = await SeedPropertyAsync(tenantId, condominiumId, "A1");
        await SeedFrontDeskContactAsync(tenantId, condominiumId, "Portaria Bloco A", "+5511977776666", isActive: false);

        var result = await ResolveAsync(tenantId, propertyId);

        result.Should().BeNull("mandate §20 — IsActive=false must behave exactly like not-configured");
    }

    [Fact]
    public async Task Reader_returns_null_when_the_property_has_no_condominium()
    {
        var tenantId = Guid.NewGuid();
        var propertyId = await SeedPropertyAsync(tenantId, condominiumId: null, code: "STANDALONE1");

        var result = await ResolveAsync(tenantId, propertyId);

        result.Should().BeNull("mandate §21 — a Property without a Condominium must never fall back to a different contact");
    }

    [Fact]
    public async Task Reader_returns_null_for_a_property_belonging_to_another_tenant()
    {
        var (ownerTenantId, condominiumId) = await SeedCondominiumAsync();
        var propertyId = await SeedPropertyAsync(ownerTenantId, condominiumId, "A1");
        await SeedFrontDeskContactAsync(ownerTenantId, condominiumId, "Portaria Bloco A", "+5511977776666", isActive: true);

        var result = await ResolveAsync(Guid.NewGuid(), propertyId);

        result.Should().BeNull("a cross-tenant propertyId must be indistinguishable from a non-existent one");
    }

    [Fact]
    public async Task Reader_never_returns_the_aggregate_only_the_minimal_shape()
    {
        var (tenantId, condominiumId) = await SeedCondominiumAsync();
        var propertyId = await SeedPropertyAsync(tenantId, condominiumId, "A1");
        await SeedFrontDeskContactAsync(tenantId, condominiumId, "Portaria Bloco A", "+5511977776666", isActive: true);

        var result = await ResolveAsync(tenantId, propertyId);

        result.Should().NotBeNull();
        result!.GetType().GetProperties().Select(p => p.Name).Should().BeEquivalentTo(["ContactId", "DisplayName", "PhoneNumber"]);
    }

    // ---- Helpers ----

    private async Task<IHostPro.Contexts.PropertyManagement.Contracts.FrontDeskContactReadResult?> ResolveAsync(Guid tenantId, Guid propertyId)
    {
        // Mirrors production DI exactly: PropertyManagementDbContext's own
        // injected ITenantContext (consumed by EF Core's Global Query
        // Filter — the FIRST layer) is already set to the caller's tenant
        // by the ambient scope BEFORE FrontDeskContactReader ever runs; the
        // reader's own internal TenantAwareTransactionScope only adds the
        // SECOND, independent layer (the RLS `SET LOCAL app.tenant_id`).
        // Passing a never-set TenantContext here (as the DbContext's own
        // constructor argument) would make the Global Query Filter itself
        // exclude every row, unrelated to whether ADR-026's resolution
        // logic is correct — a cross-tenant query (deliberately different
        // from the tenantId passed into GetActiveByPropertyIdAsync) is
        // exercised separately by Reader_returns_null_for_a_property_belonging_to_another_tenant.
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);
        await using var dbContext = CreateDbContext(_appConnectionString, tenantContext);
        var reader = new FrontDeskContactReader(dbContext);

        return await reader.GetActiveByPropertyIdAsync(tenantId, propertyId, CancellationToken.None);
    }

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

        var condominium = Condominium.Create(Guid.NewGuid(), tenantId, "Test Condominium", SomeAddress(), DateTimeOffset.UtcNow);
        dbContext.Condominiums.Add(condominium);

        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();

        return (tenantId, condominium.Id);
    }

    private async Task<Guid> SeedPropertyAsync(Guid tenantId, Guid? condominiumId, string code)
    {
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);

        await using var dbContext = CreateDbContext(_migratorConnectionString, tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        var property = Property.Create(
            Guid.NewGuid(), tenantId, PropertyCode.Create(code), "Test Property", 2, condominiumId,
            condominiumId is null ? SomeAddress() : null, DateTimeOffset.UtcNow);
        dbContext.Properties.Add(property);

        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();

        return property.Id;
    }

    private async Task<Guid> SeedFrontDeskContactAsync(Guid tenantId, Guid condominiumId, string displayName, string phoneNumber, bool isActive)
    {
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);

        await using var dbContext = CreateDbContext(_migratorConnectionString, tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        var contact = FrontDeskContact.Create(Guid.NewGuid(), tenantId, condominiumId, displayName, phoneNumber, isActive, DateTimeOffset.UtcNow);
        dbContext.FrontDeskContacts.Add(contact);

        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();

        return contact.Id;
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
}
