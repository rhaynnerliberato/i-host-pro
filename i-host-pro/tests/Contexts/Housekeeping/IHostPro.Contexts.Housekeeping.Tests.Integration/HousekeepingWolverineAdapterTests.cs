using FluentAssertions;
using IHostPro.BuildingBlocks.Messaging.Abstractions;
using IHostPro.Contexts.Housekeeping.Application;
using IHostPro.Contexts.Housekeeping.Contracts;
using IHostPro.Contexts.Housekeeping.Infrastructure.Messaging;
using IHostPro.Contexts.PropertyManagement.Contracts;
using IHostPro.Contexts.Reservations.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;

namespace IHostPro.Contexts.Housekeeping.Tests.Integration;

/// <summary>
/// Fase 6, Checkpoint 6 homologação, item 14 of the user's approved
/// protocol — focused, adapter-only tests proving each of the six thin
/// Wolverine adapters (<c>ReservationCreatedHandler</c>/
/// <c>ReservationCancelledHandler</c>/<c>PropertyCreatedHandler</c>/
/// <c>PropertyActivatedHandler</c>/<c>PropertyDeactivatedHandler</c>/
/// <c>PropertyArchivedHandler</c>) passes the correct TenantId, a real
/// Wolverine-assigned MessageId, and the intact message instance to
/// <see cref="IHousekeepingMessageExecutionScope"/> — and nothing else,
/// confirming zero business logic lives in the adapter itself.
///
/// <see cref="MessageContext"/>'s own <c>Envelope</c> property has no public
/// setter (confirmed by inspection — only Wolverine's own internal codegen
/// populates it), so a hand-built <c>MessageContext</c> cannot be passed
/// directly to <c>Handle</c> without touching a private member. Instead,
/// each test builds a minimal, real Wolverine host (no broker, no Postgres —
/// <see cref="IMessageBus.InvokeAsync{T}(T, CancellationToken, TimeSpan?)"/>
/// dispatches purely in-process) with the six adapters discovered exactly as
/// <c>IHostPro.Worker</c> discovers them, and a fake
/// <see cref="IHousekeepingMessageExecutionScope"/> registered in place of
/// the real one — the same "swap the one real dependency, dispatch through
/// Wolverine's own real routing" technique Wolverine's own documentation
/// recommends for handler-level testing, never a private-internals
/// workaround and never a broker/database stand-in for the whole app.
/// </summary>
public sealed class HousekeepingWolverineAdapterTests
{
    private sealed class CapturingExecutionScope : IHousekeepingMessageExecutionScope
    {
        public object? CapturedMessage { get; private set; }
        public Guid CapturedTenantId { get; private set; }
        public Guid CapturedMessageId { get; private set; }
        public int CallCount { get; private set; }

        public Task ExecuteAsync<TMessage>(TMessage message, Guid tenantId, Guid messageId, CancellationToken cancellationToken)
            where TMessage : IntegrationEvent
        {
            CallCount++;
            CapturedMessage = message;
            CapturedTenantId = tenantId;
            CapturedMessageId = messageId;
            return Task.CompletedTask;
        }

        public Task ExecuteCreateCleaningForReservationAsync(
            CreateCleaningForReservation command, Guid messageId, CancellationToken cancellationToken)
        {
            CallCount++;
            CapturedMessage = command;
            CapturedTenantId = command.TenantId;
            CapturedMessageId = messageId;
            return Task.CompletedTask;
        }
    }

    private static async Task<(IHost Host, CapturingExecutionScope Scope)> BuildHostAsync()
    {
        var scope = new CapturingExecutionScope();
        var hostBuilder = Host.CreateApplicationBuilder();
        hostBuilder.Services.AddSingleton<IHousekeepingMessageExecutionScope>(scope);
        hostBuilder.UseWolverine(opts =>
        {
            opts.Discovery.IncludeAssembly(typeof(ReservationCreatedHandler).Assembly);
        });

        var host = hostBuilder.Build();
        await host.StartAsync();
        return (host, scope);
    }

    [Fact]
    public async Task ReservationCreatedHandler_passes_the_intact_message_TenantId_and_a_real_MessageId_and_nothing_else()
    {
        var (host, scope) = await BuildHostAsync();
        using (host)
        {
            var tenantId = Guid.NewGuid();
            var message = new ReservationCreated
            {
                TenantId = tenantId, AggregateId = Guid.NewGuid(), AggregateType = "Reservation",
                CorrelationId = Guid.NewGuid(), ActorType = "User", ActorId = Guid.NewGuid().ToString(),
                ReservationId = Guid.NewGuid(), PropertyId = Guid.NewGuid(), Status = "confirmed",
            };

            var bus = host.Services.GetRequiredService<IMessageBus>();
            await bus.InvokeAsync(message);

            scope.CallCount.Should().Be(1, "the adapter must delegate exactly once, with no retries or extra calls");
            scope.CapturedMessage.Should().BeSameAs(message, "the adapter must pass the message through unmodified");
            scope.CapturedTenantId.Should().Be(tenantId, "the adapter must pass message.TenantId, never a value derived elsewhere");
            scope.CapturedMessageId.Should().NotBe(Guid.Empty, "the adapter must pass a real Wolverine-assigned envelope id");
        }
    }

    [Fact]
    public async Task ReservationCancelledHandler_passes_the_intact_message_TenantId_and_a_real_MessageId_and_nothing_else()
    {
        var (host, scope) = await BuildHostAsync();
        using (host)
        {
            var tenantId = Guid.NewGuid();
            var message = new ReservationCancelled
            {
                TenantId = tenantId, AggregateId = Guid.NewGuid(), AggregateType = "Reservation",
                CorrelationId = Guid.NewGuid(), ActorType = "User", ActorId = Guid.NewGuid().ToString(),
                ReservationId = Guid.NewGuid(), PropertyId = Guid.NewGuid(),
            };

            var bus = host.Services.GetRequiredService<IMessageBus>();
            await bus.InvokeAsync(message);

            scope.CallCount.Should().Be(1);
            scope.CapturedMessage.Should().BeSameAs(message);
            scope.CapturedTenantId.Should().Be(tenantId);
            scope.CapturedMessageId.Should().NotBe(Guid.Empty);
        }
    }

    [Fact]
    public async Task PropertyCreatedHandler_passes_the_intact_message_TenantId_and_a_real_MessageId_and_nothing_else()
    {
        var (host, scope) = await BuildHostAsync();
        using (host)
        {
            var tenantId = Guid.NewGuid();
            var message = new PropertyCreated
            {
                TenantId = tenantId, AggregateId = Guid.NewGuid(), AggregateType = "Property",
                CorrelationId = Guid.NewGuid(), ActorType = "System", ActorId = null,
                PropertyId = Guid.NewGuid(), Status = "draft",
            };

            var bus = host.Services.GetRequiredService<IMessageBus>();
            await bus.InvokeAsync(message);

            scope.CallCount.Should().Be(1);
            scope.CapturedMessage.Should().BeSameAs(message);
            scope.CapturedTenantId.Should().Be(tenantId);
            scope.CapturedMessageId.Should().NotBe(Guid.Empty);
        }
    }

    [Fact]
    public async Task PropertyActivatedHandler_passes_the_intact_message_TenantId_and_a_real_MessageId_and_nothing_else()
    {
        var (host, scope) = await BuildHostAsync();
        using (host)
        {
            var tenantId = Guid.NewGuid();
            var message = new PropertyActivated
            {
                TenantId = tenantId, AggregateId = Guid.NewGuid(), AggregateType = "Property",
                CorrelationId = Guid.NewGuid(), ActorType = "System", ActorId = null,
                PropertyId = Guid.NewGuid(),
            };

            var bus = host.Services.GetRequiredService<IMessageBus>();
            await bus.InvokeAsync(message);

            scope.CallCount.Should().Be(1);
            scope.CapturedMessage.Should().BeSameAs(message);
            scope.CapturedTenantId.Should().Be(tenantId);
            scope.CapturedMessageId.Should().NotBe(Guid.Empty);
        }
    }

    [Fact]
    public async Task PropertyDeactivatedHandler_passes_the_intact_message_TenantId_and_a_real_MessageId_and_nothing_else()
    {
        var (host, scope) = await BuildHostAsync();
        using (host)
        {
            var tenantId = Guid.NewGuid();
            var message = new PropertyDeactivated
            {
                TenantId = tenantId, AggregateId = Guid.NewGuid(), AggregateType = "Property",
                CorrelationId = Guid.NewGuid(), ActorType = "System", ActorId = null,
                PropertyId = Guid.NewGuid(),
            };

            var bus = host.Services.GetRequiredService<IMessageBus>();
            await bus.InvokeAsync(message);

            scope.CallCount.Should().Be(1);
            scope.CapturedMessage.Should().BeSameAs(message);
            scope.CapturedTenantId.Should().Be(tenantId);
            scope.CapturedMessageId.Should().NotBe(Guid.Empty);
        }
    }

    [Fact]
    public async Task PropertyArchivedHandler_passes_the_intact_message_TenantId_and_a_real_MessageId_and_nothing_else()
    {
        var (host, scope) = await BuildHostAsync();
        using (host)
        {
            var tenantId = Guid.NewGuid();
            var message = new PropertyArchived
            {
                TenantId = tenantId, AggregateId = Guid.NewGuid(), AggregateType = "Property",
                CorrelationId = Guid.NewGuid(), ActorType = "System", ActorId = null,
                PropertyId = Guid.NewGuid(),
            };

            var bus = host.Services.GetRequiredService<IMessageBus>();
            await bus.InvokeAsync(message);

            scope.CallCount.Should().Be(1);
            scope.CapturedMessage.Should().BeSameAs(message);
            scope.CapturedTenantId.Should().Be(tenantId);
            scope.CapturedMessageId.Should().NotBe(Guid.Empty);
        }
    }

    /// <summary>
    /// Fase 8, Checkpoint 1 (Workflow Orchestration — ADR-018): the seventh
    /// thin Wolverine adapter, for the codebase's first cross-context
    /// COMMAND rather than an IntegrationEvent — same "swap the one real
    /// dependency, dispatch through Wolverine's own real routing" proof as
    /// the six event adapters above.
    /// </summary>
    [Fact]
    public async Task CreateCleaningForReservationHandler_passes_the_intact_command_and_a_real_MessageId_and_nothing_else()
    {
        var (host, scope) = await BuildHostAsync();
        using (host)
        {
            var tenantId = Guid.NewGuid();
            var message = new CreateCleaningForReservation
            {
                TenantId = tenantId,
                ReservationId = Guid.NewGuid(),
                PropertyId = Guid.NewGuid(),
                CorrelationId = Guid.NewGuid(),
            };

            var bus = host.Services.GetRequiredService<IMessageBus>();
            await bus.InvokeAsync(message);

            scope.CallCount.Should().Be(1, "the adapter must delegate exactly once, with no retries or extra calls");
            scope.CapturedMessage.Should().BeSameAs(message, "the adapter must pass the command through unmodified");
            scope.CapturedTenantId.Should().Be(tenantId, "the adapter must pass command.TenantId, never a value derived elsewhere");
            scope.CapturedMessageId.Should().NotBe(Guid.Empty, "the adapter must pass a real Wolverine-assigned envelope id");
        }
    }
}
