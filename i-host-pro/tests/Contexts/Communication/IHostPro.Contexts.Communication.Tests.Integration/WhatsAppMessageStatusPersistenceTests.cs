using FluentAssertions;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.Contexts.Communication.Application;
using IHostPro.Contexts.Communication.Domain;
using IHostPro.Contexts.Communication.Infrastructure;
using IHostPro.Contexts.Communication.Infrastructure.Persistence;
using IHostPro.Contexts.ExternalIntegrations.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace IHostPro.Contexts.Communication.Tests.Integration;

/// <summary>
/// Fase 9, Checkpoint 2.3.3 (ADR-022 item 14) — drives
/// <see cref="ICommunicationMessageExecutionScope"/> with a real
/// <see cref="WhatsAppMessageStatusChanged"/> event against a real
/// PostgreSQL instance (mandate §36). Reuses
/// <see cref="CommunicationMessageExecutionScopeTests.Fixture"/> — same
/// container, same migrated schema, no new fixture needed.
/// </summary>
public class WhatsAppMessageStatusPersistenceTests : IClassFixture<CommunicationMessageExecutionScopeTests.Fixture>
{
    private readonly CommunicationMessageExecutionScopeTests.Fixture _fixture;

    public WhatsAppMessageStatusPersistenceTests(CommunicationMessageExecutionScopeTests.Fixture fixture) => _fixture = fixture;

    // ---- Composition root -------------------------------------------------

    private ServiceProvider BuildServiceProvider()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Communication"] = _fixture.AppConnectionString })
            .Build();

        var services = new ServiceCollection();
        services.AddScoped<ITenantContext, TenantContext>();
        services.AddLogging();
        services.AddCommunicationModule(configuration);
        services.AddCommunicationWhatsAppStatusConsumer();

        return services.BuildServiceProvider();
    }

    private static WhatsAppMessageStatusChanged NewEvent(
        Guid tenantId, string providerMessageId, WhatsAppMessageProviderStatus status, DateTimeOffset occurredAtUtc, int? providerErrorCode = null) => new()
    {
        TenantId = tenantId,
        AggregateId = Guid.NewGuid(),
        AggregateType = "WhatsAppMessageStatus",
        CorrelationId = Guid.NewGuid(),
        ActorType = "Integration",
        ProviderMessageId = providerMessageId,
        Status = status,
        OccurredAtUtc = occurredAtUtc,
        ProviderErrorCode = providerErrorCode,
    };

    private static async Task ExecuteAsync(ServiceProvider serviceProvider, WhatsAppMessageStatusChanged @event)
    {
        await using var scope = serviceProvider.CreateAsyncScope();
        var executionScope = scope.ServiceProvider.GetRequiredService<ICommunicationMessageExecutionScope>();
        await executionScope.ExecuteAsync(@event, @event.TenantId, Guid.NewGuid(), CancellationToken.None);
    }

    private async Task<Guid> SeedSentMessageAsync(Guid tenantId, string providerMessageId, DateTimeOffset sentAt)
    {
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);
        await using var dbContext = CreateDbContext(tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        var message = Message.Create(
            Guid.NewGuid(), tenantId, Guid.NewGuid(), "WhatsApp", "RESERVATION_CONFIRMATION",
            null, "Olá, sua reserva foi confirmada.", $"pg-{Guid.NewGuid():N}", sentAt);
        message.MarkQueued();
        message.MarkSending();
        message.MarkSent(sentAt, providerMessageId);

        dbContext.Messages.Add(message);
        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();
        return message.Id;
    }

    // ---- Lookup + tenant isolation -----------------------------------------

    [Fact]
    public async Task ExecuteAsync_applies_a_status_to_the_Message_found_by_ProviderMessageId()
    {
        var tenantId = Guid.NewGuid();
        var providerMessageId = $"wamid.{Guid.NewGuid():N}";
        var sentAt = DateTimeOffset.UtcNow;
        await SeedSentMessageAsync(tenantId, providerMessageId, sentAt);
        using var serviceProvider = BuildServiceProvider();

        await ExecuteAsync(serviceProvider, NewEvent(tenantId, providerMessageId, WhatsAppMessageProviderStatus.Delivered, sentAt.AddSeconds(1)));

        var message = await ReadMessageAsync(tenantId, providerMessageId);
        message.Should().NotBeNull();
        message!.Status.Should().Be(MessageStatus.Delivered);
        message.DeliveredAtUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task ExecuteAsync_never_lets_a_DIFFERENT_tenants_RLS_scoped_connection_see_the_Message()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var providerMessageId = $"wamid.{Guid.NewGuid():N}";
        var sentAt = DateTimeOffset.UtcNow;
        await SeedSentMessageAsync(tenantA, providerMessageId, sentAt);
        using var serviceProvider = BuildServiceProvider();

        await ExecuteAsync(serviceProvider, NewEvent(tenantA, providerMessageId, WhatsAppMessageProviderStatus.Delivered, sentAt.AddSeconds(1)));

        (await ReadMessageAsync(tenantB, providerMessageId)).Should().BeNull(
            "a different tenant's RLS-scoped connection must never see this tenant's Message even knowing its ProviderMessageId");
    }

    // ---- Terminal statuses --------------------------------------------------

    [Fact]
    public async Task ExecuteAsync_persists_Read()
    {
        var tenantId = Guid.NewGuid();
        var providerMessageId = $"wamid.{Guid.NewGuid():N}";
        var sentAt = DateTimeOffset.UtcNow;
        await SeedSentMessageAsync(tenantId, providerMessageId, sentAt);
        using var serviceProvider = BuildServiceProvider();

        await ExecuteAsync(serviceProvider, NewEvent(tenantId, providerMessageId, WhatsAppMessageProviderStatus.Read, sentAt.AddSeconds(2)));

        var message = await ReadMessageAsync(tenantId, providerMessageId);
        message!.Status.Should().Be(MessageStatus.Read);
        message.ReadAtUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task ExecuteAsync_persists_Failed_with_the_provider_error_code()
    {
        var tenantId = Guid.NewGuid();
        var providerMessageId = $"wamid.{Guid.NewGuid():N}";
        var sentAt = DateTimeOffset.UtcNow;
        await SeedSentMessageAsync(tenantId, providerMessageId, sentAt);
        using var serviceProvider = BuildServiceProvider();

        await ExecuteAsync(serviceProvider, NewEvent(tenantId, providerMessageId, WhatsAppMessageProviderStatus.Failed, sentAt.AddSeconds(2), 131026));

        var message = await ReadMessageAsync(tenantId, providerMessageId);
        message!.Status.Should().Be(MessageStatus.Failed);
        message.FailedAtUtc.Should().NotBeNull();
        message.FailureReason.Should().Be("provider_error_131026");
    }

    // ---- Idempotency (duplicate/regression never mutate) --------------------

    [Fact]
    public async Task ExecuteAsync_never_mutates_on_a_duplicate_status()
    {
        var tenantId = Guid.NewGuid();
        var providerMessageId = $"wamid.{Guid.NewGuid():N}";
        var sentAt = DateTimeOffset.UtcNow;
        await SeedSentMessageAsync(tenantId, providerMessageId, sentAt);
        using var serviceProvider = BuildServiceProvider();
        await ExecuteAsync(serviceProvider, NewEvent(tenantId, providerMessageId, WhatsAppMessageProviderStatus.Delivered, sentAt.AddSeconds(1)));
        var deliveredAt = (await ReadMessageAsync(tenantId, providerMessageId))!.DeliveredAtUtc;

        await ExecuteAsync(serviceProvider, NewEvent(tenantId, providerMessageId, WhatsAppMessageProviderStatus.Delivered, sentAt.AddSeconds(5)));

        var message = await ReadMessageAsync(tenantId, providerMessageId);
        message!.Status.Should().Be(MessageStatus.Delivered);
        message.DeliveredAtUtc.Should().Be(deliveredAt, "a duplicate report must never overwrite the original timestamp");
    }

    [Fact]
    public async Task ExecuteAsync_never_mutates_on_a_regressive_status()
    {
        var tenantId = Guid.NewGuid();
        var providerMessageId = $"wamid.{Guid.NewGuid():N}";
        var sentAt = DateTimeOffset.UtcNow;
        await SeedSentMessageAsync(tenantId, providerMessageId, sentAt);
        using var serviceProvider = BuildServiceProvider();
        await ExecuteAsync(serviceProvider, NewEvent(tenantId, providerMessageId, WhatsAppMessageProviderStatus.Read, sentAt.AddSeconds(1)));

        // Read is terminal for Failed purposes — a Regression, never applied.
        await ExecuteAsync(serviceProvider, NewEvent(tenantId, providerMessageId, WhatsAppMessageProviderStatus.Failed, sentAt.AddSeconds(5)));

        var message = await ReadMessageAsync(tenantId, providerMessageId);
        message!.Status.Should().Be(MessageStatus.Read);
        message.FailedAtUtc.Should().BeNull();
    }

    // ---- DB access ----------------------------------------------------------

    private async Task<Message?> ReadMessageAsync(Guid tenantId, string providerMessageId)
    {
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);
        await using var dbContext = CreateDbContext(tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        var message = await dbContext.Messages.AsNoTracking()
            .FirstOrDefaultAsync(m => m.TenantId == tenantId && m.ProviderMessageId == providerMessageId);

        await transaction.CommitAsync();
        return message;
    }

    private static async Task SetTenantAsync(CommunicationDbContext dbContext, Guid tenantId) =>
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.tenant_id', {tenantId.ToString()}, true)");

    private CommunicationDbContext CreateDbContext(ITenantContext tenantContext)
    {
        var options = new DbContextOptionsBuilder<CommunicationDbContext>()
            .UseNpgsql(_fixture.MigratorConnectionString, npgsqlOptions => npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "communication"))
            .Options;
        return new CommunicationDbContext(options, tenantContext);
    }
}
