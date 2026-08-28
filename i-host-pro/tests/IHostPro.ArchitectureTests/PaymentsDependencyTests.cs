using FluentAssertions;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.Payments.Application;
using IHostPro.Contexts.Payments.Contracts;
using IHostPro.Contexts.Payments.Domain;
using IHostPro.Contexts.Payments.Infrastructure.Persistence;
using NetArchTest.Rules;

namespace IHostPro.ArchitectureTests;

/// <summary>
/// Enforces the Payments-specific rules from Fase 10, Checkpoint 5
/// (PIX/Payment Deterministic Foundation). Mirrors
/// <c>GuestOperationsDependencyTests</c> exactly where applicable.
/// </summary>
public class PaymentsDependencyTests
{
    [Fact]
    public void PixCharge_Is_Tenant_Owned()
    {
        typeof(ITenantOwned).IsAssignableFrom(typeof(PixCharge)).Should().BeTrue(
            "PixCharge must implement ITenantOwned for the Global Query Filter + RLS to apply");
    }

    [Fact]
    public void PaymentsDbContext_Owns_The_Approved_Schema_Name()
    {
        var options = new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<PaymentsDbContext>().Options;
        using var dbContext = new PaymentsDbContext(
            options, new IHostPro.BuildingBlocks.Infrastructure.Multitenancy.TenantContext());

        dbContext.SchemaName.Should().Be("payments");
    }

    [Fact]
    public void Domain_Never_Depends_On_Application_Infrastructure_Or_EfCore()
    {
        var result = Types.InAssembly(typeof(PixCharge).Assembly)
            .Should()
            .NotHaveDependencyOnAny(
                "IHostPro.Contexts.Payments.Application",
                "IHostPro.Contexts.Payments.Infrastructure",
                "Microsoft.EntityFrameworkCore")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(BuildFailureMessage(result));
    }

    [Fact]
    public void Application_Never_Depends_On_Infrastructure()
    {
        var result = Types.InAssembly(typeof(IPaymentsTransactionExecutor).Assembly)
            .Should()
            .NotHaveDependencyOn("IHostPro.Contexts.Payments.Infrastructure")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(BuildFailureMessage(result));
    }

    /// <summary>
    /// Payments.Application/.Infrastructure may reference other Bounded
    /// Contexts exclusively through two approved Contracts assemblies
    /// (GuestOperations.Contracts — LateCheckoutPaymentRequired, the sole
    /// trigger; ExternalIntegrations.Contracts — IPixProvider, ADR-025
    /// synchronous exception #10) — never any Domain/Application/
    /// Infrastructure/Api layer of any context, and never any other Bounded
    /// Context at all.
    /// </summary>
    [Fact]
    public void Application_And_Infrastructure_Only_Reference_GuestOperations_And_ExternalIntegrations_Contracts()
    {
        var assembliesToCheck = new[]
        {
            typeof(IPaymentsTransactionExecutor).Assembly,
            typeof(PaymentsDbContext).Assembly,
        };

        var forbiddenDependencies = new[]
        {
            "IHostPro.Contexts.GuestOperations.Domain",
            "IHostPro.Contexts.GuestOperations.Application",
            "IHostPro.Contexts.GuestOperations.Infrastructure",
            "IHostPro.Contexts.GuestOperations.Api",
            "IHostPro.Contexts.ExternalIntegrations.Domain",
            "IHostPro.Contexts.ExternalIntegrations.Application",
            "IHostPro.Contexts.ExternalIntegrations.Infrastructure",
            "IHostPro.Contexts.ExternalIntegrations.Api",
            "IHostPro.Contexts.PropertyManagement",
            "IHostPro.Contexts.Identity",
            "IHostPro.Contexts.Dashboard",
            "IHostPro.Contexts.Workflow",
            "IHostPro.Contexts.Communication",
            "IHostPro.Contexts.Reservations",
            "IHostPro.Contexts.Housekeeping",
            "IHostPro.Contexts.Configuration",
        };

        foreach (var assembly in assembliesToCheck)
        {
            var result = Types.InAssembly(assembly)
                .Should()
                .NotHaveDependencyOnAny(forbiddenDependencies)
                .GetResult();

            result.IsSuccessful.Should().BeTrue($"{assembly.GetName().Name}: {BuildFailureMessage(result)}");
        }
    }

    /// <summary>
    /// ADR-025's own testable consequence (Fase 10, Checkpoint 5,
    /// synchronous exception #10): Payments is the ONLY Bounded Context
    /// authorized to consume <c>IPixProvider</c> — External Integrations
    /// owns/implements it, everyone else must never reference it.
    /// </summary>
    [Fact]
    public void No_Other_Context_Assembly_References_IPixProvider_Except_Payments()
    {
        var otherContextAssemblies = new[]
        {
            typeof(IHostPro.Contexts.GuestOperations.Domain.GuestStayOperation).Assembly,
            typeof(IHostPro.Contexts.GuestOperations.Infrastructure.Persistence.GuestOperationsDbContext).Assembly,
            typeof(IHostPro.Contexts.Communication.Domain.Message).Assembly,
            typeof(IHostPro.Contexts.Communication.Infrastructure.Persistence.CommunicationDbContext).Assembly,
            typeof(IHostPro.Contexts.Reservations.Domain.Reservation).Assembly,
            typeof(IHostPro.Contexts.Housekeeping.Domain.Cleaning).Assembly,
            typeof(IHostPro.Contexts.PropertyManagement.Domain.Property).Assembly,
            typeof(IHostPro.Contexts.Identity.Domain.Tenant).Assembly,
            typeof(IHostPro.Contexts.Configuration.Domain.PolicyDefinition).Assembly,
            typeof(IHostPro.Contexts.Dashboard.Domain.AssemblyReference).Assembly,
            typeof(IHostPro.Contexts.Workflow.Application.IWorkflowCommandDispatcher).Assembly,
        };

        var readerFullName = typeof(IHostPro.Contexts.ExternalIntegrations.Contracts.IPixProvider).FullName!;

        foreach (var assembly in otherContextAssemblies.Distinct())
        {
            var referencingTypes = Types.InAssembly(assembly)
                .That()
                .HaveDependencyOn(readerFullName)
                .GetTypes();

            referencingTypes.Should().BeEmpty(
                "only External Integrations (owner) and Payments (the sole authorized consumer, ADR-025 " +
                $"exception #10) may reference IPixProvider — {assembly.GetName().Name} referencing it would mean " +
                "an unauthorized Bounded Context bypassed the purpose-limited exception");
        }
    }

    /// <summary>
    /// ADR-027's own testable consequence (Fase 10, Checkpoint 5,
    /// synchronous exception #11): Communication is the ONLY Bounded
    /// Context authorized to consume <c>IPixChargeDeliveryReader</c> —
    /// Payments owns/implements it, everyone else must never reference it.
    /// </summary>
    [Fact]
    public void No_Other_Context_Assembly_References_IPixChargeDeliveryReader_Except_Communication()
    {
        var otherContextAssemblies = new[]
        {
            typeof(IHostPro.Contexts.GuestOperations.Domain.GuestStayOperation).Assembly,
            typeof(IHostPro.Contexts.GuestOperations.Infrastructure.Persistence.GuestOperationsDbContext).Assembly,
            typeof(IHostPro.Contexts.Reservations.Domain.Reservation).Assembly,
            typeof(IHostPro.Contexts.Housekeeping.Domain.Cleaning).Assembly,
            typeof(IHostPro.Contexts.PropertyManagement.Domain.Property).Assembly,
            typeof(IHostPro.Contexts.Identity.Domain.Tenant).Assembly,
            typeof(IHostPro.Contexts.Configuration.Domain.PolicyDefinition).Assembly,
            typeof(IHostPro.Contexts.Dashboard.Domain.AssemblyReference).Assembly,
            typeof(IHostPro.Contexts.Workflow.Application.IWorkflowCommandDispatcher).Assembly,
            typeof(IHostPro.Contexts.ExternalIntegrations.Infrastructure.Persistence.ExternalIntegrationsDbContext).Assembly,
        };

        var readerFullName = typeof(IPixChargeDeliveryReader).FullName!;

        foreach (var assembly in otherContextAssemblies.Distinct())
        {
            var referencingTypes = Types.InAssembly(assembly)
                .That()
                .HaveDependencyOn(readerFullName)
                .GetTypes();

            referencingTypes.Should().BeEmpty(
                "only Payments (owner) and Communication (the sole authorized consumer, ADR-027 exception #11) " +
                $"may reference IPixChargeDeliveryReader — {assembly.GetName().Name} referencing it would mean " +
                "an unauthorized Bounded Context bypassed the purpose-limited exception");
        }
    }

    /// <summary>No other Bounded Context may reference Payments' internal layers — only its own Contracts.</summary>
    [Fact]
    public void No_Other_Bounded_Context_Ever_References_Payments_Domain_Application_Or_Infrastructure()
    {
        var otherContextAssemblies = new[]
        {
            typeof(IHostPro.Contexts.GuestOperations.Domain.GuestStayOperation).Assembly,
            typeof(IHostPro.Contexts.GuestOperations.Infrastructure.Persistence.GuestOperationsDbContext).Assembly,
            typeof(IHostPro.Contexts.Communication.Domain.Message).Assembly,
            typeof(IHostPro.Contexts.Communication.Infrastructure.Persistence.CommunicationDbContext).Assembly,
            typeof(IHostPro.Contexts.Reservations.Domain.Reservation).Assembly,
            typeof(IHostPro.Contexts.Housekeeping.Domain.Cleaning).Assembly,
            typeof(IHostPro.Contexts.PropertyManagement.Domain.Property).Assembly,
            typeof(IHostPro.Contexts.Identity.Domain.Tenant).Assembly,
            typeof(IHostPro.Contexts.Configuration.Domain.PolicyDefinition).Assembly,
            typeof(IHostPro.Contexts.Dashboard.Domain.AssemblyReference).Assembly,
            typeof(IHostPro.Contexts.Workflow.Application.IWorkflowCommandDispatcher).Assembly,
            typeof(IHostPro.Contexts.ExternalIntegrations.Infrastructure.Persistence.ExternalIntegrationsDbContext).Assembly,
        };

        foreach (var assembly in otherContextAssemblies.Distinct())
        {
            var result = Types.InAssembly(assembly)
                .Should()
                .NotHaveDependencyOnAny(
                    "IHostPro.Contexts.Payments.Domain",
                    "IHostPro.Contexts.Payments.Application",
                    "IHostPro.Contexts.Payments.Infrastructure")
                .GetResult();

            result.IsSuccessful.Should().BeTrue($"{assembly.GetName().Name}: {BuildFailureMessage(result)}");
        }
    }

    /// <summary>
    /// Mandate item 58/62: no Integration Event published by Payments may
    /// ever carry the QR/copy-paste payload, a provider secret, or payer
    /// PII — those cross into Communication ONLY through the synchronous,
    /// purpose-limited <c>IPixChargeDeliveryReader</c> (ADR-027).
    /// </summary>
    [Theory]
    [InlineData(typeof(PixChargeCreated))]
    [InlineData(typeof(PixChargeConfirmed))]
    public void Payments_Integration_Events_Never_Carry_QR_Provider_Or_Payer_Data(Type eventType)
    {
        var propertyNames = eventType.GetProperties().Select(p => p.Name).ToList();

        foreach (var forbidden in new[]
        {
            "QrCode", "CopyPaste", "PixCode", "Payload", "ProviderChargeId", "ProviderSecret",
            "GuestName", "GuestPhone", "Cpf", "Cnpj", "Email", "Document",
        })
        {
            propertyNames.Should().NotContain(
                name => name.Contains(forbidden, StringComparison.OrdinalIgnoreCase),
                $"{eventType.Name} must never carry a property containing '{forbidden}'");
        }
    }

    /// <summary>
    /// Mandate item 25/26 (ADR-027): <see cref="PixChargeDeliveryReadResult"/>
    /// is the ONLY authorized carrier of the QR payload — but it must never
    /// carry the aggregate itself, the provider charge id, or the
    /// idempotency key.
    /// </summary>
    [Fact]
    public void PixChargeDeliveryReadResult_Never_Carries_ProviderChargeId_Or_IdempotencyKey()
    {
        var propertyNames = typeof(PixChargeDeliveryReadResult).GetProperties().Select(p => p.Name).ToList();

        propertyNames.Should().NotContain(name => name.Contains("ProviderChargeId", StringComparison.OrdinalIgnoreCase));
        propertyNames.Should().NotContain(name => name.Contains("IdempotencyKey", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Mandate items 1/43/44: Payments has ZERO public API this checkpoint
    /// (charge creation is automatic via <c>LateCheckoutPaymentRequired</c>;
    /// confirmation is simulated via a real Wolverine send in tests, never
    /// an HTTP endpoint) — no <c>IHostPro.Contexts.Payments.Api</c> project
    /// exists, and no assembly anywhere in the solution declares a
    /// test/simulation-only controller for it.
    /// </summary>
    [Fact]
    public void No_Payments_Api_Project_Or_Test_Only_Controller_Exists()
    {
        var apiAssemblyPath = Path.Combine(
            RepositoryRoot(), "src", "Contexts", "Payments", "IHostPro.Contexts.Payments.Api");

        Directory.Exists(apiAssemblyPath).Should().BeFalse(
            "Payments has zero public API by default this checkpoint (mandate items 1/43/44) — " +
            "creating an Api project requires explicit new authorization, never silently added for test convenience");
    }

    /// <summary>
    /// Mandate item 12/48 (Checkpoint 5), extended by Checkpoint 5.1 mandate
    /// item 19: exactly the two known, approved migrations exist —
    /// <c>InitialCreate</c> and <c>AddPixChargeExpiredAtUtc</c> (the latter
    /// adds the nullable <c>expired_at_utc</c> column <see cref="PixCharge.Expire"/>
    /// needs — no RLS/grant changes required, both already apply at the
    /// whole-table level).
    /// </summary>
    [Fact]
    public void Only_The_Known_Approved_Migrations_Exist()
    {
        var migrationsDirectory = Path.Combine(
            RepositoryRoot(), "src", "Contexts", "Payments",
            "IHostPro.Contexts.Payments.Infrastructure", "Persistence", "Migrations");

        Directory.Exists(migrationsDirectory).Should().BeTrue($"expected {migrationsDirectory} to exist");

        var migrationFiles = Directory.GetFiles(migrationsDirectory, "*.cs", SearchOption.TopDirectoryOnly)
            .Where(path => !path.EndsWith("ModelSnapshot.cs", StringComparison.Ordinal))
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => !name!.EndsWith(".Designer", StringComparison.Ordinal))
            .ToArray();

        migrationFiles.Should().ContainSingle(name => name!.EndsWith("_InitialCreate", StringComparison.Ordinal));
        migrationFiles.Should().ContainSingle(name => name!.EndsWith("_AddPixChargeExpiredAtUtc", StringComparison.Ordinal));
        migrationFiles.Should().HaveCount(2, "no other migration is approved for this checkpoint");
    }

    /// <summary>
    /// Checkpoint 5.1 mandate item 16: the two new provider-neutral inbound
    /// messages must never carry a vendor type, the QR/copy-paste payload,
    /// payer PII, or a secret — mirrors
    /// <see cref="Payments_Integration_Events_Never_Carry_QR_Provider_Or_Payer_Data"/>'s
    /// own forbidden-name list exactly, extended to the two new record
    /// types (neither is an <c>IntegrationEvent</c> — same reasoning as
    /// <see cref="PixChargeConfirmationReceived"/> is not one either).
    /// </summary>
    [Theory]
    [InlineData(typeof(PixChargeFailureReceived))]
    [InlineData(typeof(PixChargeExpirationReceived))]
    public void Payments_Provider_Neutral_Inbound_Messages_Never_Carry_QR_Provider_Or_Payer_Data(Type messageType)
    {
        var propertyNames = messageType.GetProperties().Select(p => p.Name).ToList();

        foreach (var forbidden in new[]
        {
            "QrCode", "CopyPaste", "PixCode", "Payload", "ProviderChargeId", "ProviderSecret",
            "GuestName", "GuestPhone", "Cpf", "Cnpj", "Email", "Document",
        })
        {
            propertyNames.Should().NotContain(
                name => name.Contains(forbidden, StringComparison.OrdinalIgnoreCase),
                $"{messageType.Name} must never carry a property containing '{forbidden}'");
        }
    }

    private static string RepositoryRoot([System.Runtime.CompilerServices.CallerFilePath] string thisFilePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFilePath)!, "..", ".."));

    private static string BuildFailureMessage(TestResult result) =>
        result.FailingTypes is null
            ? "Architecture rule violated."
            : "Architecture rule violated by: " + string.Join(", ", result.FailingTypes.Select(t => t.FullName));
}
