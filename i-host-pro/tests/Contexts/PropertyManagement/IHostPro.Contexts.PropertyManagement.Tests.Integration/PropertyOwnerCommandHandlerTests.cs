using System.Security.Cryptography;
using FluentAssertions;
using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.BuildingBlocks.Infrastructure.Messaging;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.BuildingBlocks.Infrastructure.Persistence;
using IHostPro.Contexts.Identity.Contracts;
using IHostPro.Contexts.Identity.Contracts.Authorization;
using IHostPro.Contexts.Identity.Domain;
using IHostPro.Contexts.Identity.Domain.ValueObjects;
using IHostPro.Contexts.Identity.Infrastructure;
using IHostPro.Contexts.Identity.Infrastructure.Authorization;
using IHostPro.Contexts.Identity.Infrastructure.Persistence;
using IHostPro.Contexts.Identity.Infrastructure.Security;
using IHostPro.Contexts.PropertyManagement.Application;
using IHostPro.Contexts.PropertyManagement.Application.Errors;
using IHostPro.Contexts.PropertyManagement.Application.Owners;
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
using Microsoft.Extensions.Options;
using Npgsql;
using Testcontainers.PostgreSql;
using Wolverine;
using Wolverine.EntityFrameworkCore;

namespace IHostPro.Contexts.PropertyManagement.Tests.Integration;

/// <summary>
/// End-to-end test of the LinkPropertyOwner/UnlinkPropertyOwner/
/// ListPropertyOwners/ListMyProperties/GetMyPropertyDetail use cases against
/// REAL PostgreSQL (both the <c>identity</c> and <c>property_management</c>
/// schemas — Link genuinely calls Identity's
/// <see cref="IIdentityUserEligibilityReader"/>, so this file provisions both,
/// mirroring <c>PropertiesLifecycleEndpointsTests.Fixture</c>) — dispatched
/// through the REAL production composition root via <see cref="ISender"/>,
/// mirroring <see cref="PropertyLifecycleCommandHandlerTests"/>'s structure.
/// Only Property Management's own outbox schema is provisioned for Wolverine
/// enrollment: no Identity command is ever dispatched here, only its
/// read-only eligibility reader.
/// </summary>
public class PropertyOwnerCommandHandlerTests : IClassFixture<PropertyOwnerCommandHandlerTests.Fixture>
{
    private const string OutboxSchema = "property_management_messaging";
    private const string KnownPassword = "Correct-Horse-Battery-Staple-42!";

    private readonly string _migratorConnectionString;
    private readonly string _appConnectionString;
    private readonly string _signingKeyPem;

    public PropertyOwnerCommandHandlerTests(Fixture fixture)
    {
        _migratorConnectionString = fixture.MigratorConnectionString;
        _appConnectionString = fixture.AppConnectionString;

        using var signingKey = RSA.Create(2048);
        _signingKeyPem = signingKey.ExportRSAPrivateKeyPem();
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

            await using (var identityDbContext = CreateIdentityDbContext(MigratorConnectionString))
            {
                await identityDbContext.Database.MigrateAsync();
            }
            await using (var pmDbContext = CreateDbContext(MigratorConnectionString, new TenantContext()))
            {
                await pmDbContext.Database.MigrateAsync();
            }

            await ProvisionOutboxAsMigratorAsync();
        }

        public async Task DisposeAsync() => await _container.DisposeAsync();

        private static IdentityDbContext CreateIdentityDbContext(string connectionString)
        {
            var options = new DbContextOptionsBuilder<IdentityDbContext>()
                .UseNpgsql(connectionString, npgsqlOptions =>
                    npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "identity"))
                .Options;

            return new IdentityDbContext(options, new TenantContext());
        }

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

    // ---- Service graph (real composition root, both modules) -------------------

    private async Task<IHost> BuildHostAsync(Action<IServiceCollection>? overrides = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Identity"] = _appConnectionString,
            ["ConnectionStrings:PropertyManagement"] = _appConnectionString,
            ["Identity:Jwt:Issuer"] = "https://identity.ihostpro.test",
            ["Identity:Jwt:Audience"] = "ihostpro-api-test",
            ["Identity:Jwt:AccessTokenLifetime"] = "00:15:00",
            ["Identity:Jwt:ClockSkew"] = "00:01:00",
            ["Identity:Jwt:SigningKey:PrivateKeyPem"] = _signingKeyPem,
            ["Identity:AccountLockout:MaxFailedAccessAttempts"] = "5",
            ["Identity:AccountLockout:DefaultLockoutDuration"] = "00:05:00",
            ["Identity:AccountLockout:AllowedForNewUsers"] = "true",
            ["Identity:RefreshToken:Lifetime"] = "30.00:00:00",
            ["Identity:RefreshToken:SecretSizeBytes"] = "32",
            ["Identity:RefreshToken:ConcurrentRotationGraceWindow"] = "00:00:10",
        }).Build();

        var hostBuilder = Host.CreateApplicationBuilder();
        hostBuilder.Services.AddScoped<ITenantContext, TenantContext>();
        hostBuilder.Services.AddScoped<ICurrentTenantProvider, TenantContextCurrentTenantProvider>();
        hostBuilder.Services.AddIHostProTenantAwarePipeline();
        hostBuilder.Services.AddIdentityModule(configuration, isDevelopmentEnvironment: false);
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

    private static async Task<Result> ExecuteAsync<TMessage>(IHost host, TMessage message, Guid tenantId)
        where TMessage : IRequest<Result>
    {
        using var scope = host.Services.CreateScope();
        var sp = scope.ServiceProvider;
        sp.GetRequiredService<ITenantContext>().SetTenant(tenantId);

        return await sp.GetRequiredService<ISender>().Send(message, CancellationToken.None);
    }

    // ---- Seeding: Property Management --------------------------------------

    private static async Task<Guid> SeedPropertyAsync(IHost host, Guid tenantId, string code)
    {
        var command = new CreatePropertyCommand(tenantId, Guid.NewGuid(), code, $"Property {code}", 2, null, SomeAddress);
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

    // ---- Seeding: Identity (tenant + eligible/ineligible owners) -----------

    private async Task<Guid> SeedTenantAsync()
    {
        var tenantId = Guid.NewGuid();
        await using var dbContext = CreateIdentityDbContext(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);

        var tenant = Tenant.Provision(
            tenantId, TenantSlug.Create($"tenant-{Guid.NewGuid():N}"[..20]), "Test Tenant", DateTimeOffset.UtcNow);
        dbContext.Tenants.Add(tenant);

        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();

        return tenantId;
    }

    private async Task<Guid> SeedIdentityUserAsync(Guid tenantId, bool blocked = false, string[]? roleCodes = null)
    {
        await using var dbContext = CreateIdentityDbContext(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);

        var hasher = new Argon2PasswordHasher(new KonsciousArgon2idPrimitive(), Options.Create(new Argon2Options()));
        var hash = PasswordHash.FromEncoded(hasher.HashPassword(null!, KnownPassword));
        var now = DateTimeOffset.UtcNow;
        var user = User.Register(Guid.NewGuid(), tenantId, Email.Create($"{Guid.NewGuid():N}@ihostpro.com"), "Test Owner", hash, now);
        if (blocked)
            user.Block(now);
        dbContext.Users.Add(user);

        foreach (var roleCode in roleCodes ?? [])
            dbContext.UserRoles.Add(new UserRole(tenantId, user.Id, roleCode, now, assignedByUserId: null));

        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();

        return user.Id;
    }

    private async Task<Guid> SeedEligibleOwnerAsync(Guid tenantId) =>
        await SeedIdentityUserAsync(tenantId, roleCodes: [IdentityRoleCodes.PropertyOwner]);

    private async Task RemoveOwnerRoleAsync(Guid tenantId, Guid userId)
    {
        await using var dbContext = CreateIdentityDbContext(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);

        var role = await dbContext.UserRoles.SingleAsync(ur => ur.UserId == userId && ur.RoleCode == IdentityRoleCodes.PropertyOwner);
        dbContext.UserRoles.Remove(role);

        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();
    }

    private IdentityDbContext CreateIdentityDbContext(Guid tenantId)
    {
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);
        var options = new DbContextOptionsBuilder<IdentityDbContext>()
            .UseNpgsql(_appConnectionString, npgsqlOptions =>
                npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "identity"))
            .Options;

        return new IdentityDbContext(options, tenantContext);
    }

    private static async Task SetPostgresTenantAsync(IdentityDbContext dbContext, Guid tenantId) =>
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.tenant_id', {tenantId.ToString()}, true)");

    private static async Task SetPostgresTenantAsync(PropertyManagementDbContext dbContext, Guid tenantId) =>
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.tenant_id', {tenantId.ToString()}, true)");

    // ---- Link: happy path across every Property status ----------------------

    [Theory]
    [InlineData("draft")]
    [InlineData("active")]
    [InlineData("inactive")]
    [InlineData("archived")]
    public async Task Linking_an_eligible_owner_succeeds_regardless_of_property_status(string status)
    {
        var tenantId = await SeedTenantAsync();
        var ownerId = await SeedEligibleOwnerAsync(tenantId);
        using var host = await BuildHostAsync();
        var propertyId = status switch
        {
            "draft" => await SeedPropertyAsync(host, tenantId, "OWN-1"),
            "active" => await SeedActivePropertyAsync(host, tenantId, "OWN-1"),
            "inactive" => await SeedInactivePropertyAsync(host, tenantId, "OWN-1"),
            "archived" => await SeedArchivedPropertyAsync(host, tenantId, "OWN-1"),
            _ => throw new InvalidOperationException(),
        };

        var result = await ExecuteAsync<LinkPropertyOwnerCommand, PropertyOwnerResult>(
            host, new LinkPropertyOwnerCommand(tenantId, Guid.NewGuid(), propertyId, ownerId), tenantId);

        result.IsSuccess.Should().BeTrue();
        result.Value.OwnerUserId.Should().Be(ownerId);
    }

    [Fact]
    public async Task Linking_never_changes_the_propertys_status()
    {
        var tenantId = await SeedTenantAsync();
        var ownerId = await SeedEligibleOwnerAsync(tenantId);
        using var host = await BuildHostAsync();
        var propertyId = await SeedActivePropertyAsync(host, tenantId, "OWN-2");

        await ExecuteAsync<LinkPropertyOwnerCommand, PropertyOwnerResult>(
            host, new LinkPropertyOwnerCommand(tenantId, Guid.NewGuid(), propertyId, ownerId), tenantId);

        await using var dbContext = CreateDbContext(_migratorConnectionString, TenantContextFor(tenantId));
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);
        var property = await dbContext.Properties.SingleAsync(p => p.Id == propertyId);
        property.Status.Should().Be(PropertyStatus.Active);
    }

    [Fact]
    public async Task Linking_persists_one_audit_entry_and_the_link_row()
    {
        var tenantId = await SeedTenantAsync();
        var ownerId = await SeedEligibleOwnerAsync(tenantId);
        using var host = await BuildHostAsync();
        var propertyId = await SeedPropertyAsync(host, tenantId, "OWN-3");

        var result = await ExecuteAsync<LinkPropertyOwnerCommand, PropertyOwnerResult>(
            host, new LinkPropertyOwnerCommand(tenantId, Guid.NewGuid(), propertyId, ownerId), tenantId);

        result.IsSuccess.Should().BeTrue();

        await using var dbContext = CreateDbContext(_migratorConnectionString, TenantContextFor(tenantId));
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);
        var link = await dbContext.PropertyOwnerLinks.SingleAsync(l => l.PropertyId == propertyId && l.OwnerUserId == ownerId);
        link.TenantId.Should().Be(tenantId);
        var auditEntries = await dbContext.PropertyAuditLog.Where(e => e.AggregateId == propertyId).ToListAsync();
        auditEntries.Should().ContainSingle(e => e.ActionCode == "property_owner_linked");
    }

    // ---- Link: rejections ---------------------------------------------------

    [Fact]
    public async Task Linking_a_nonexistent_owner_user_fails_with_404_and_persists_nothing()
    {
        var tenantId = await SeedTenantAsync();
        using var host = await BuildHostAsync();
        var propertyId = await SeedPropertyAsync(host, tenantId, "OWN-4");

        var result = await ExecuteAsync<LinkPropertyOwnerCommand, PropertyOwnerResult>(
            host, new LinkPropertyOwnerCommand(tenantId, Guid.NewGuid(), propertyId, Guid.NewGuid()), tenantId);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(PropertyManagementErrorCodes.OwnerUserNotFound);
    }

    [Fact]
    public async Task Linking_a_blocked_owner_fails_with_OwnerUserNotEligible()
    {
        var tenantId = await SeedTenantAsync();
        var ownerId = await SeedIdentityUserAsync(tenantId, blocked: true, roleCodes: [IdentityRoleCodes.PropertyOwner]);
        using var host = await BuildHostAsync();
        var propertyId = await SeedPropertyAsync(host, tenantId, "OWN-5");

        var result = await ExecuteAsync<LinkPropertyOwnerCommand, PropertyOwnerResult>(
            host, new LinkPropertyOwnerCommand(tenantId, Guid.NewGuid(), propertyId, ownerId), tenantId);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(PropertyManagementErrorCodes.OwnerUserNotEligible);
    }

    [Fact]
    public async Task Linking_an_active_user_without_the_role_fails_with_OwnerUserNotEligible()
    {
        var tenantId = await SeedTenantAsync();
        var ownerId = await SeedIdentityUserAsync(tenantId, roleCodes: ["OPERATOR"]);
        using var host = await BuildHostAsync();
        var propertyId = await SeedPropertyAsync(host, tenantId, "OWN-6");

        var result = await ExecuteAsync<LinkPropertyOwnerCommand, PropertyOwnerResult>(
            host, new LinkPropertyOwnerCommand(tenantId, Guid.NewGuid(), propertyId, ownerId), tenantId);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(PropertyManagementErrorCodes.OwnerUserNotEligible);
    }

    [Fact]
    public async Task Linking_to_a_nonexistent_property_fails_with_PropertyNotFound()
    {
        var tenantId = await SeedTenantAsync();
        var ownerId = await SeedEligibleOwnerAsync(tenantId);
        using var host = await BuildHostAsync();

        var result = await ExecuteAsync<LinkPropertyOwnerCommand, PropertyOwnerResult>(
            host, new LinkPropertyOwnerCommand(tenantId, Guid.NewGuid(), Guid.NewGuid(), ownerId), tenantId);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(PropertyManagementErrorCodes.PropertyNotFound);
    }

    [Fact]
    public async Task Linking_an_already_linked_pair_fails_with_PropertyOwnerAlreadyLinked()
    {
        var tenantId = await SeedTenantAsync();
        var ownerId = await SeedEligibleOwnerAsync(tenantId);
        using var host = await BuildHostAsync();
        var propertyId = await SeedPropertyAsync(host, tenantId, "OWN-7");
        await ExecuteAsync<LinkPropertyOwnerCommand, PropertyOwnerResult>(
            host, new LinkPropertyOwnerCommand(tenantId, Guid.NewGuid(), propertyId, ownerId), tenantId);

        var result = await ExecuteAsync<LinkPropertyOwnerCommand, PropertyOwnerResult>(
            host, new LinkPropertyOwnerCommand(tenantId, Guid.NewGuid(), propertyId, ownerId), tenantId);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(PropertyManagementErrorCodes.PropertyOwnerAlreadyLinked);
    }

    // ---- Link: cross-tenant / absent tenant ----------------------------------

    [Fact]
    public async Task Linking_a_property_belonging_to_a_different_tenant_fails_with_PropertyNotFound()
    {
        var tenantA = await SeedTenantAsync();
        var tenantB = await SeedTenantAsync();
        var ownerInTenantB = await SeedEligibleOwnerAsync(tenantB);
        using var host = await BuildHostAsync();
        var propertyInTenantA = await SeedPropertyAsync(host, tenantA, "OWN-8");

        var result = await ExecuteAsync<LinkPropertyOwnerCommand, PropertyOwnerResult>(
            host, new LinkPropertyOwnerCommand(tenantB, Guid.NewGuid(), propertyInTenantA, ownerInTenantB), tenantB);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(PropertyManagementErrorCodes.PropertyNotFound);
    }

    /// <summary>
    /// Unlike the lifecycle commands (which throw
    /// <see cref="TenantContextNotResolvedException"/> from their own wrapping
    /// pipeline behavior), <see cref="LinkPropertyOwnerCommand"/> has NO
    /// wrapping behavior — the FIRST tenant-dependent read it performs is
    /// Identity's eligibility check. In this composed host, Identity's
    /// <c>IdentityDbContext</c> and Property Management's own ambient
    /// <see cref="ITenantContext"/> are the SAME DI-scoped instance, and
    /// <c>BaseDbContext</c>'s mandatory Global Query Filter
    /// (<c>entity.TenantId == _tenantContext.TenantId</c>) fails closed to
    /// zero rows — never an exception — when that ambient tenant is
    /// unresolved (<c>TenantId</c> is <c>null</c>, matching no row). So an
    /// absent ambient tenant here surfaces as <c>OwnerUserNotFound</c> (the
    /// eligibility read finds nothing), not as a thrown exception — the
    /// request still fails closed, just earlier and via a different signal
    /// than the lifecycle commands'. Even a genuinely eligible, real owner
    /// (seeded below) cannot reach Property Management's own
    /// executor-level tenant check: the Identity read fails first, every
    /// time, precisely because it shares the same unresolved ambient context.
    /// </summary>
    [Fact]
    public async Task Absent_tenant_context_fails_closed_on_link_via_OwnerUserNotFound()
    {
        var tenantId = await SeedTenantAsync();
        var ownerId = await SeedEligibleOwnerAsync(tenantId);
        using var host = await BuildHostAsync();
        using var scope = host.Services.CreateScope();

        var result = await scope.ServiceProvider.GetRequiredService<ISender>()
            .Send(new LinkPropertyOwnerCommand(tenantId, Guid.NewGuid(), Guid.NewGuid(), ownerId), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(PropertyManagementErrorCodes.OwnerUserNotFound);
    }

    // ---- Link: rollback -------------------------------------------------------

    private sealed class ThrowingPropertyAuditWriter : IPropertyAuditWriter
    {
        public void Record(PropertyAuditEntry entry) =>
            throw new InvalidOperationException("Simulated failure after the link was staged.");
    }

    [Fact]
    public async Task A_failure_after_the_link_was_staged_rolls_back_with_no_partial_state()
    {
        var tenantId = await SeedTenantAsync();
        var ownerId = await SeedEligibleOwnerAsync(tenantId);
        using var seedHost = await BuildHostAsync();
        var propertyId = await SeedPropertyAsync(seedHost, tenantId, "OWN-9");

        using var host = await BuildHostAsync(overrides: sc =>
            sc.AddScoped<IPropertyAuditWriter, ThrowingPropertyAuditWriter>());

        var act = async () => await ExecuteAsync<LinkPropertyOwnerCommand, PropertyOwnerResult>(
            host, new LinkPropertyOwnerCommand(tenantId, Guid.NewGuid(), propertyId, ownerId), tenantId);

        await act.Should().ThrowAsync<InvalidOperationException>();

        await using var dbContext = CreateDbContext(_migratorConnectionString, TenantContextFor(tenantId));
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);
        (await dbContext.PropertyOwnerLinks.CountAsync(l => l.PropertyId == propertyId)).Should().Be(0);
    }

    // ---- Link: TOCTOU (Checkpoint 5 plan, item 6/17) -------------------------

    /// <summary>
    /// A decorator around the real <see cref="IIdentityUserEligibilityReader"/>
    /// that, right after truthfully reporting the owner as eligible, removes
    /// the role directly in PostgreSQL — deterministically reproducing the
    /// accepted TOCTOU window ("o papel/estado do proprietário pode mudar
    /// entre a validação e o commit") without relying on real thread
    /// interleaving. The link must still confirm: the eligibility check
    /// already ran and passed, and Checkpoint 5's plan explicitly forbids any
    /// retry of it.
    /// </summary>
    private sealed class RoleRevokingEligibilityReader : IIdentityUserEligibilityReader
    {
        private readonly IIdentityUserEligibilityReader _inner;
        private readonly Func<Guid, Guid, Task> _revokeRole;

        public RoleRevokingEligibilityReader(IIdentityUserEligibilityReader inner, Func<Guid, Guid, Task> revokeRole)
        {
            _inner = inner;
            _revokeRole = revokeRole;
        }

        public async Task<IdentityUserEligibility?> GetAsync(
            Guid tenantId, Guid userId, string requiredRoleCode, CancellationToken cancellationToken)
        {
            var result = await _inner.GetAsync(tenantId, userId, requiredRoleCode, cancellationToken);
            if (result is { IsActive: true, HasRequiredRole: true })
                await _revokeRole(tenantId, userId);

            return result;
        }
    }

    [Fact]
    public async Task A_role_removed_after_eligibility_passed_but_before_commit_does_not_prevent_the_link_from_confirming()
    {
        var tenantId = await SeedTenantAsync();
        var ownerId = await SeedEligibleOwnerAsync(tenantId);
        using var seedHost = await BuildHostAsync();
        var propertyId = await SeedPropertyAsync(seedHost, tenantId, "OWN-10");

        using var host = await BuildHostAsync(overrides: sc =>
        {
            sc.AddScoped<IIdentityUserEligibilityReader>(sp =>
            {
                var real = new IdentityUserEligibilityReader(sp.GetRequiredService<IdentityDbContext>());
                return new RoleRevokingEligibilityReader(real, RemoveOwnerRoleAsync);
            });
        });

        var result = await ExecuteAsync<LinkPropertyOwnerCommand, PropertyOwnerResult>(
            host, new LinkPropertyOwnerCommand(tenantId, Guid.NewGuid(), propertyId, ownerId), tenantId);

        result.IsSuccess.Should().BeTrue("the link must confirm — no retry of the eligibility check is performed");

        await using var dbContext = CreateDbContext(_migratorConnectionString, TenantContextFor(tenantId));
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);
        (await dbContext.PropertyOwnerLinks.CountAsync(l => l.PropertyId == propertyId && l.OwnerUserId == ownerId)).Should().Be(1);

        await using var verifyIdentity = CreateIdentityDbContext(tenantId);
        await using var verifyTransaction = await verifyIdentity.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(verifyIdentity, tenantId);
        (await verifyIdentity.UserRoles.CountAsync(ur => ur.UserId == ownerId && ur.RoleCode == IdentityRoleCodes.PropertyOwner)).Should().Be(0);
    }

    // ---- Link: real concurrency -----------------------------------------------

    /// <summary>
    /// Unlike <c>PropertyLifecycleCommandHandlerTests</c>'s own
    /// <c>BarrierPropertyAuditWriter</c> (which only synchronizes, since none
    /// of its tests assert on the audit table), this one ALSO genuinely
    /// stages the entry via the real <see cref="PropertyAuditWriter"/>
    /// behavior — this file's concurrency tests assert the winner's audit
    /// entry actually persisted, so a synchronization-only stand-in would
    /// silently drop it.
    /// </summary>
    private sealed class BarrierPropertyAuditWriter : IPropertyAuditWriter
    {
        private readonly PropertyManagementDbContext _dbContext;
        private readonly Barrier _barrier;

        public BarrierPropertyAuditWriter(PropertyManagementDbContext dbContext, Barrier barrier)
        {
            _dbContext = dbContext;
            _barrier = barrier;
        }

        public void Record(PropertyAuditEntry entry)
        {
            _dbContext.PropertyAuditLog.Add(entry);
            _barrier.SignalAndWait(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task Two_concurrent_links_of_the_same_pair_allow_only_one_to_succeed()
    {
        var tenantId = await SeedTenantAsync();
        var ownerId = await SeedEligibleOwnerAsync(tenantId);
        using var seedHost = await BuildHostAsync();
        var propertyId = await SeedPropertyAsync(seedHost, tenantId, "OWN-11");

        using var barrier = new Barrier(2);
        using var hostA = await BuildHostAsync(overrides: sc =>
            sc.AddScoped<IPropertyAuditWriter>(sp => new BarrierPropertyAuditWriter(sp.GetRequiredService<PropertyManagementDbContext>(), barrier)));
        using var hostB = await BuildHostAsync(overrides: sc =>
            sc.AddScoped<IPropertyAuditWriter>(sp => new BarrierPropertyAuditWriter(sp.GetRequiredService<PropertyManagementDbContext>(), barrier)));

        var taskA = ExecuteAsync<LinkPropertyOwnerCommand, PropertyOwnerResult>(
            hostA, new LinkPropertyOwnerCommand(tenantId, Guid.NewGuid(), propertyId, ownerId), tenantId);
        var taskB = ExecuteAsync<LinkPropertyOwnerCommand, PropertyOwnerResult>(
            hostB, new LinkPropertyOwnerCommand(tenantId, Guid.NewGuid(), propertyId, ownerId), tenantId);
        var results = await Task.WhenAll(taskA, taskB);

        results.Count(r => r.IsSuccess).Should().Be(1);
        var failure = results.Single(r => r.IsFailure);
        failure.Error.Code.Should().Be(PropertyManagementErrorCodes.PropertyOwnerAlreadyLinked);

        await using var dbContext = CreateDbContext(_migratorConnectionString, TenantContextFor(tenantId));
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);
        (await dbContext.PropertyOwnerLinks.CountAsync(l => l.PropertyId == propertyId && l.OwnerUserId == ownerId)).Should().Be(1);
        (await dbContext.PropertyAuditLog.CountAsync(e => e.AggregateId == propertyId && e.ActionCode == "property_owner_linked")).Should().Be(1);
    }

    // ---- Unlink: happy path ---------------------------------------------------

    [Fact]
    public async Task Unlinking_an_existing_link_succeeds_and_persists_the_removal_and_audit()
    {
        var tenantId = await SeedTenantAsync();
        var ownerId = await SeedEligibleOwnerAsync(tenantId);
        using var host = await BuildHostAsync();
        var propertyId = await SeedPropertyAsync(host, tenantId, "OWN-12");
        await ExecuteAsync<LinkPropertyOwnerCommand, PropertyOwnerResult>(
            host, new LinkPropertyOwnerCommand(tenantId, Guid.NewGuid(), propertyId, ownerId), tenantId);

        var result = await ExecuteAsync(host, new UnlinkPropertyOwnerCommand(tenantId, Guid.NewGuid(), propertyId, ownerId), tenantId);

        result.IsSuccess.Should().BeTrue();

        await using var dbContext = CreateDbContext(_migratorConnectionString, TenantContextFor(tenantId));
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);
        (await dbContext.PropertyOwnerLinks.CountAsync(l => l.PropertyId == propertyId && l.OwnerUserId == ownerId)).Should().Be(0);
        (await dbContext.PropertyAuditLog.CountAsync(e => e.AggregateId == propertyId && e.ActionCode == "property_owner_unlinked")).Should().Be(1);
    }

    [Theory]
    [InlineData("active")]
    [InlineData("archived")]
    public async Task Unlinking_succeeds_regardless_of_property_status(string status)
    {
        var tenantId = await SeedTenantAsync();
        var ownerId = await SeedEligibleOwnerAsync(tenantId);
        using var host = await BuildHostAsync();
        var propertyId = status == "active"
            ? await SeedActivePropertyAsync(host, tenantId, "OWN-13")
            : await SeedArchivedPropertyAsync(host, tenantId, "OWN-13");
        await ExecuteAsync<LinkPropertyOwnerCommand, PropertyOwnerResult>(
            host, new LinkPropertyOwnerCommand(tenantId, Guid.NewGuid(), propertyId, ownerId), tenantId);

        var result = await ExecuteAsync(host, new UnlinkPropertyOwnerCommand(tenantId, Guid.NewGuid(), propertyId, ownerId), tenantId);

        result.IsSuccess.Should().BeTrue();
    }

    // ---- Unlink: rejections ----------------------------------------------------

    [Fact]
    public async Task Unlinking_a_nonexistent_property_fails_with_PropertyNotFound()
    {
        var tenantId = await SeedTenantAsync();
        using var host = await BuildHostAsync();

        var result = await ExecuteAsync(host, new UnlinkPropertyOwnerCommand(tenantId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()), tenantId);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(PropertyManagementErrorCodes.PropertyNotFound);
    }

    [Fact]
    public async Task Unlinking_a_link_that_does_not_exist_fails_with_PropertyOwnerNotLinked()
    {
        var tenantId = await SeedTenantAsync();
        using var host = await BuildHostAsync();
        var propertyId = await SeedPropertyAsync(host, tenantId, "OWN-14");

        var result = await ExecuteAsync(host, new UnlinkPropertyOwnerCommand(tenantId, Guid.NewGuid(), propertyId, Guid.NewGuid()), tenantId);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(PropertyManagementErrorCodes.PropertyOwnerNotLinked);
    }

    [Fact]
    public async Task Repeating_an_unlink_fails_the_second_time_with_no_second_audit_entry()
    {
        var tenantId = await SeedTenantAsync();
        var ownerId = await SeedEligibleOwnerAsync(tenantId);
        using var host = await BuildHostAsync();
        var propertyId = await SeedPropertyAsync(host, tenantId, "OWN-15");
        await ExecuteAsync<LinkPropertyOwnerCommand, PropertyOwnerResult>(
            host, new LinkPropertyOwnerCommand(tenantId, Guid.NewGuid(), propertyId, ownerId), tenantId);
        await ExecuteAsync(host, new UnlinkPropertyOwnerCommand(tenantId, Guid.NewGuid(), propertyId, ownerId), tenantId);

        var second = await ExecuteAsync(host, new UnlinkPropertyOwnerCommand(tenantId, Guid.NewGuid(), propertyId, ownerId), tenantId);

        second.IsFailure.Should().BeTrue();
        second.Error.Code.Should().Be(PropertyManagementErrorCodes.PropertyOwnerNotLinked);

        await using var dbContext = CreateDbContext(_migratorConnectionString, TenantContextFor(tenantId));
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);
        (await dbContext.PropertyAuditLog.CountAsync(e => e.AggregateId == propertyId && e.ActionCode == "property_owner_unlinked")).Should().Be(1);
    }

    [Fact]
    public async Task Absent_tenant_context_fails_closed_on_unlink()
    {
        using var host = await BuildHostAsync();
        using var scope = host.Services.CreateScope();

        var act = async () => await scope.ServiceProvider.GetRequiredService<ISender>()
            .Send(new UnlinkPropertyOwnerCommand(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()), CancellationToken.None);

        await act.Should().ThrowAsync<TenantContextNotResolvedException>();
    }

    // ---- Unlink: real concurrency -----------------------------------------------

    [Fact]
    public async Task Two_concurrent_unlinks_of_the_same_link_allow_only_one_to_succeed()
    {
        var tenantId = await SeedTenantAsync();
        var ownerId = await SeedEligibleOwnerAsync(tenantId);
        using var seedHost = await BuildHostAsync();
        var propertyId = await SeedPropertyAsync(seedHost, tenantId, "OWN-16");
        await ExecuteAsync<LinkPropertyOwnerCommand, PropertyOwnerResult>(
            seedHost, new LinkPropertyOwnerCommand(tenantId, Guid.NewGuid(), propertyId, ownerId), tenantId);

        using var barrier = new Barrier(2);
        using var hostA = await BuildHostAsync(overrides: sc =>
            sc.AddScoped<IPropertyAuditWriter>(sp => new BarrierPropertyAuditWriter(sp.GetRequiredService<PropertyManagementDbContext>(), barrier)));
        using var hostB = await BuildHostAsync(overrides: sc =>
            sc.AddScoped<IPropertyAuditWriter>(sp => new BarrierPropertyAuditWriter(sp.GetRequiredService<PropertyManagementDbContext>(), barrier)));

        var taskA = ExecuteAsync(hostA, new UnlinkPropertyOwnerCommand(tenantId, Guid.NewGuid(), propertyId, ownerId), tenantId);
        var taskB = ExecuteAsync(hostB, new UnlinkPropertyOwnerCommand(tenantId, Guid.NewGuid(), propertyId, ownerId), tenantId);
        var results = await Task.WhenAll(taskA, taskB);

        results.Count(r => r.IsSuccess).Should().Be(1);
        var failure = results.Single(r => r.IsFailure);
        failure.Error.Code.Should().Be(PropertyManagementErrorCodes.PropertyOwnerNotLinked);

        await using var dbContext = CreateDbContext(_migratorConnectionString, TenantContextFor(tenantId));
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);
        (await dbContext.PropertyOwnerLinks.CountAsync(l => l.PropertyId == propertyId && l.OwnerUserId == ownerId)).Should().Be(0);
        (await dbContext.PropertyAuditLog.CountAsync(e => e.AggregateId == propertyId && e.ActionCode == "property_owner_unlinked")).Should().Be(1);
    }

    // ---- Queries: ListPropertyOwners / ListMyProperties / GetMyPropertyDetail ----

    [Fact]
    public async Task ListPropertyOwners_returns_the_linked_owner()
    {
        var tenantId = await SeedTenantAsync();
        var ownerId = await SeedEligibleOwnerAsync(tenantId);
        using var host = await BuildHostAsync();
        var propertyId = await SeedPropertyAsync(host, tenantId, "OWN-17");
        await ExecuteAsync<LinkPropertyOwnerCommand, PropertyOwnerResult>(
            host, new LinkPropertyOwnerCommand(tenantId, Guid.NewGuid(), propertyId, ownerId), tenantId);

        var result = await ExecuteAsync<ListPropertyOwnersQuery, PagedResult<PropertyOwnerResult>>(
            host, new ListPropertyOwnersQuery(propertyId, null, null), tenantId);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().ContainSingle(o => o.OwnerUserId == ownerId);
    }

    [Fact]
    public async Task ListMyProperties_returns_properties_of_every_status_linked_to_the_owner()
    {
        var tenantId = await SeedTenantAsync();
        var ownerId = await SeedEligibleOwnerAsync(tenantId);
        using var host = await BuildHostAsync();
        var draftId = await SeedPropertyAsync(host, tenantId, "OWN-18A");
        var archivedId = await SeedArchivedPropertyAsync(host, tenantId, "OWN-18B");
        await ExecuteAsync<LinkPropertyOwnerCommand, PropertyOwnerResult>(
            host, new LinkPropertyOwnerCommand(tenantId, Guid.NewGuid(), draftId, ownerId), tenantId);
        await ExecuteAsync<LinkPropertyOwnerCommand, PropertyOwnerResult>(
            host, new LinkPropertyOwnerCommand(tenantId, Guid.NewGuid(), archivedId, ownerId), tenantId);

        var result = await ExecuteAsync<ListMyPropertiesQuery, PagedResult<PropertySummaryResult>>(
            host, new ListMyPropertiesQuery(ownerId, null, null), tenantId);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Select(p => p.Id).Should().BeEquivalentTo([draftId, archivedId]);
    }

    [Fact]
    public async Task GetMyPropertyDetail_returns_404_when_the_property_is_not_linked_to_the_caller()
    {
        var tenantId = await SeedTenantAsync();
        var ownerId = await SeedEligibleOwnerAsync(tenantId);
        using var host = await BuildHostAsync();
        var propertyId = await SeedPropertyAsync(host, tenantId, "OWN-19");

        var result = await ExecuteAsync<GetMyPropertyDetailQuery, PropertyResult>(
            host, new GetMyPropertyDetailQuery(ownerId, propertyId), tenantId);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(PropertyManagementErrorCodes.PropertyNotFound);
    }

    [Fact]
    public async Task GetMyPropertyDetail_returns_404_for_a_property_linked_to_a_different_owner()
    {
        var tenantId = await SeedTenantAsync();
        var ownerId = await SeedEligibleOwnerAsync(tenantId);
        var otherOwnerId = await SeedEligibleOwnerAsync(tenantId);
        using var host = await BuildHostAsync();
        var propertyId = await SeedPropertyAsync(host, tenantId, "OWN-20");
        await ExecuteAsync<LinkPropertyOwnerCommand, PropertyOwnerResult>(
            host, new LinkPropertyOwnerCommand(tenantId, Guid.NewGuid(), propertyId, otherOwnerId), tenantId);

        var result = await ExecuteAsync<GetMyPropertyDetailQuery, PropertyResult>(
            host, new GetMyPropertyDetailQuery(ownerId, propertyId), tenantId);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(PropertyManagementErrorCodes.PropertyNotFound);
    }

    // ---- Helpers ---------------------------------------------------------------

    private static ITenantContext TenantContextFor(Guid tenantId)
    {
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);
        return tenantContext;
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
