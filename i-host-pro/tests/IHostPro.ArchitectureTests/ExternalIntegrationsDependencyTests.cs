using System.Reflection;
using FluentAssertions;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.Communication.Domain;
using IHostPro.Contexts.ExternalIntegrations.Application;
using IHostPro.Contexts.ExternalIntegrations.Contracts;
using IHostPro.Contexts.ExternalIntegrations.Domain;
using IHostPro.Contexts.ExternalIntegrations.Infrastructure.Persistence;
using NetArchTest.Rules;

namespace IHostPro.ArchitectureTests;

/// <summary>
/// Enforces the External Integrations-specific rules from Fase 9, Checkpoint
/// 2.1 (External Integrations + Credential/Configuration Foundation) and
/// ADR-021 (External Integrations ACL and Synchronous Provider Boundary).
/// </summary>
public class ExternalIntegrationsDependencyTests
{
    /// <summary>
    /// ADR-021's central structural decision: no <c>ExternalIntegrations.Abstractions</c>
    /// project/assembly exists — the CP2.0 → CP2.1 documentary conflict was
    /// resolved by rejecting that project type, never by creating it.
    /// </summary>
    [Fact]
    public void No_ExternalIntegrations_Abstractions_Assembly_Exists()
    {
        var loadedAssemblyNames = AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetName().Name)
            .Where(name => name is not null)
            .ToList();

        loadedAssemblyNames.Should().NotContain(
            "IHostPro.Contexts.ExternalIntegrations.Abstractions",
            "ADR-021 rejected a second cross-context-referenceable project type — " +
            "ExternalIntegrations.Contracts is the only public surface, exactly like every other Bounded Context");
    }

    [Fact]
    public void Domain_Should_Not_Depend_On_Application_Infrastructure_Or_EfCore()
    {
        var result = Types.InAssembly(typeof(WhatsAppIntegration).Assembly)
            .Should()
            .NotHaveDependencyOnAny(
                "IHostPro.Contexts.ExternalIntegrations.Application",
                "IHostPro.Contexts.ExternalIntegrations.Infrastructure",
                "Microsoft.EntityFrameworkCore")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(BuildFailureMessage(result));
    }

    [Fact]
    public void Application_Should_Not_Depend_On_Infrastructure()
    {
        var result = Types.InAssembly(typeof(IWhatsAppCredentialProvider).Assembly)
            .Should()
            .NotHaveDependencyOn("IHostPro.Contexts.ExternalIntegrations.Infrastructure")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(BuildFailureMessage(result));
    }

    /// <summary>
    /// ADR-021, item 6: <c>ExternalIntegrations.Infrastructure</c> implements
    /// the contract defined in its own <c>Contracts</c> project — it never
    /// referenc es <c>Communication.Application</c>/<c>Domain</c>/
    /// <c>Infrastructure</c>. No dependency inversion hidden behind the ACL.
    /// </summary>
    [Fact]
    public void Infrastructure_Never_References_Communication()
    {
        var result = Types.InAssembly(typeof(ExternalIntegrationsDbContext).Assembly)
            .Should()
            .NotHaveDependencyOnAny(
                "IHostPro.Contexts.Communication.Domain",
                "IHostPro.Contexts.Communication.Application",
                "IHostPro.Contexts.Communication.Infrastructure")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(BuildFailureMessage(result));
    }

    /// <summary>
    /// ADR-021, item 3: Communication may reference ONLY
    /// <c>ExternalIntegrations.Contracts</c> — never <c>Domain</c>/
    /// <c>Application</c>/<c>Infrastructure</c>/<c>Api</c>. Checkpoint 2.1
    /// does not yet wire Communication to call <see cref="IMessagingProvider"/>
    /// (that belongs to Checkpoint 2.2 — no artificial wiring was added just
    /// to exercise this rule early), so this test only proves the forbidden
    /// half never happens; it does not require the Contracts reference to
    /// exist yet.
    /// </summary>
    [Fact]
    public void Communication_Never_References_ExternalIntegrations_Domain_Application_Infrastructure_Or_Api()
    {
        var assembliesToCheck = new[]
        {
            typeof(Message).Assembly,
            typeof(IHostPro.Contexts.Communication.Application.IOutboundMessageConnector).Assembly,
            typeof(IHostPro.Contexts.Communication.Infrastructure.Persistence.CommunicationDbContext).Assembly,
        };

        var forbiddenDependencies = new[]
        {
            "IHostPro.Contexts.ExternalIntegrations.Domain",
            "IHostPro.Contexts.ExternalIntegrations.Application",
            "IHostPro.Contexts.ExternalIntegrations.Infrastructure",
            "IHostPro.Contexts.ExternalIntegrations.Api",
        };

        foreach (var assembly in assembliesToCheck.Distinct())
        {
            var result = Types.InAssembly(assembly)
                .Should()
                .NotHaveDependencyOnAny(forbiddenDependencies)
                .GetResult();

            result.IsSuccessful.Should().BeTrue($"{assembly.GetName().Name}: {BuildFailureMessage(result)}");
        }
    }

    /// <summary>
    /// ADR-021, item 11: the sixth synchronous exception authorizes
    /// Communication → External Integrations exclusively — no other Bounded
    /// Context gains the right to reference <c>ExternalIntegrations.Contracts</c>
    /// or call <see cref="IMessagingProvider"/> by this ADR.
    /// </summary>
    [Fact]
    public void No_Other_Bounded_Context_Assembly_References_ExternalIntegrationsContracts_Except_Communication()
    {
        var otherContextAssemblies = new[]
        {
            typeof(IHostPro.Contexts.Reservations.Domain.Reservation).Assembly,
            typeof(IHostPro.Contexts.Reservations.Contracts.AssemblyReference).Assembly,
            typeof(IHostPro.Contexts.Reservations.Infrastructure.Persistence.ReservationsDbContext).Assembly,
            typeof(IHostPro.Contexts.Housekeeping.Domain.Cleaning).Assembly,
            typeof(IHostPro.Contexts.Housekeeping.Infrastructure.Persistence.HousekeepingDbContext).Assembly,
            typeof(IHostPro.Contexts.PropertyManagement.Domain.Property).Assembly,
            typeof(IHostPro.Contexts.PropertyManagement.Infrastructure.Persistence.PropertyManagementDbContext).Assembly,
            typeof(IHostPro.Contexts.Identity.Domain.Tenant).Assembly,
            typeof(IHostPro.Contexts.Identity.Infrastructure.Persistence.IdentityDbContext).Assembly,
            typeof(IHostPro.Contexts.Configuration.Domain.PolicyDefinition).Assembly,
            typeof(IHostPro.Contexts.Configuration.Infrastructure.Persistence.ConfigurationDbContext).Assembly,
            typeof(IHostPro.Contexts.Dashboard.Domain.AssemblyReference).Assembly,
            typeof(IHostPro.Contexts.Dashboard.Infrastructure.Persistence.DashboardDbContext).Assembly,
            typeof(IHostPro.Contexts.Workflow.Application.IWorkflowCommandDispatcher).Assembly,
            typeof(IHostPro.Contexts.Workflow.Infrastructure.Messaging.ReservationCreatedHandler).Assembly,
        };

        foreach (var assembly in otherContextAssemblies.Distinct())
        {
            var result = Types.InAssembly(assembly)
                .Should()
                .NotHaveDependencyOn("IHostPro.Contexts.ExternalIntegrations.Contracts")
                .GetResult();

            result.IsSuccessful.Should().BeTrue($"{assembly.GetName().Name}: {BuildFailureMessage(result)}");
        }
    }

    /// <summary>Closed-loop rule, mirroring every other Supporting/ACL Bounded Context's own test.</summary>
    [Fact]
    public void No_Other_Bounded_Context_Ever_References_ExternalIntegrations()
    {
        var otherContextAssemblies = new[]
        {
            typeof(IHostPro.Contexts.Reservations.Domain.Reservation).Assembly,
            typeof(IHostPro.Contexts.Reservations.Contracts.AssemblyReference).Assembly,
            typeof(IHostPro.Contexts.Reservations.Infrastructure.Persistence.ReservationsDbContext).Assembly,
            typeof(IHostPro.Contexts.Housekeeping.Domain.Cleaning).Assembly,
            typeof(IHostPro.Contexts.Housekeeping.Infrastructure.Persistence.HousekeepingDbContext).Assembly,
            typeof(IHostPro.Contexts.PropertyManagement.Domain.Property).Assembly,
            typeof(IHostPro.Contexts.PropertyManagement.Infrastructure.Persistence.PropertyManagementDbContext).Assembly,
            typeof(IHostPro.Contexts.Identity.Domain.Tenant).Assembly,
            typeof(IHostPro.Contexts.Identity.Infrastructure.Persistence.IdentityDbContext).Assembly,
            typeof(IHostPro.Contexts.Configuration.Domain.PolicyDefinition).Assembly,
            typeof(IHostPro.Contexts.Configuration.Infrastructure.Persistence.ConfigurationDbContext).Assembly,
            typeof(IHostPro.Contexts.Dashboard.Domain.AssemblyReference).Assembly,
            typeof(IHostPro.Contexts.Dashboard.Infrastructure.Persistence.DashboardDbContext).Assembly,
            typeof(IHostPro.Contexts.Workflow.Application.IWorkflowCommandDispatcher).Assembly,
            typeof(IHostPro.Contexts.Workflow.Infrastructure.Messaging.ReservationCreatedHandler).Assembly,
        };

        foreach (var assembly in otherContextAssemblies.Distinct())
        {
            var result = Types.InAssembly(assembly)
                .Should()
                .NotHaveDependencyOnAny(
                    "IHostPro.Contexts.ExternalIntegrations.Domain",
                    "IHostPro.Contexts.ExternalIntegrations.Application",
                    "IHostPro.Contexts.ExternalIntegrations.Infrastructure",
                    "IHostPro.Contexts.ExternalIntegrations.Api")
                .GetResult();

            result.IsSuccessful.Should().BeTrue($"{assembly.GetName().Name}: {BuildFailureMessage(result)}");
        }
    }

    /// <summary>
    /// ADR-021, item 5: <see cref="IMessagingProvider"/> and its request/
    /// result types are provider-neutral — no type/namespace naming a real
    /// provider (Meta, Graph API, WhatsApp Cloud API, Twilio, wamid, etc.)
    /// may appear in <c>ExternalIntegrations.Contracts</c>.
    /// </summary>
    [Fact]
    public void Contracts_Assembly_Names_No_Real_Provider()
    {
        var forbiddenSubstrings = new[] { "Meta", "Graph", "Twilio", "Wamid", "CloudApi" };

        var typeNames = Types.InAssembly(typeof(IMessagingProvider).Assembly)
            .GetTypes()
            .Select(t => t.FullName ?? t.Name)
            .ToList();

        foreach (var forbidden in forbiddenSubstrings)
        {
            typeNames.Should().NotContain(
                name => name.Contains(forbidden, StringComparison.OrdinalIgnoreCase),
                $"ExternalIntegrations.Contracts must stay provider-neutral (ADR-021) — no type name may reference '{forbidden}'");
        }
    }

    /// <summary>
    /// CP2.1 mandate §8: no outbound Integration Event exists yet — the real
    /// send/status events (<c>WhatsAppMessageSent</c>/<c>WhatsAppMessageReceived</c>/
    /// <c>WhatsAppWebhookFailed</c>, Documento 07 §16) belong to a later
    /// checkpoint. Nothing in <c>ExternalIntegrations.Contracts</c> may
    /// inherit <see cref="IHostPro.BuildingBlocks.Messaging.Abstractions.IntegrationEvent"/>
    /// yet — that would mean PII (phone/body) risked reaching the broker via
    /// an event this checkpoint never authorized.
    /// </summary>
    [Fact]
    public void Contracts_Declares_No_IntegrationEvent_Yet()
    {
        var result = Types.InAssembly(typeof(IMessagingProvider).Assembly)
            .That()
            .Inherit(typeof(IHostPro.BuildingBlocks.Messaging.Abstractions.IntegrationEvent))
            .GetTypes();

        result.Should().BeEmpty(
            "CP2.1 is foundation-only — no outbound Integration Event was authorized this checkpoint (mandate §8)");
    }

    /// <summary>
    /// CP2.1 mandate §14/§17: <see cref="WhatsAppIntegration"/> may only ever
    /// persist opaque secret REFERENCES — never a property that looks like it
    /// holds a real secret value (an access token, app secret, or verify
    /// token itself, as opposed to a named reference to one).
    /// </summary>
    [Fact]
    public void WhatsAppIntegration_Never_Declares_A_Raw_Secret_Property()
    {
        var propertyNames = typeof(WhatsAppIntegration)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToList();

        var suspiciousNames = propertyNames
            .Where(name =>
                (name.Contains("Token", StringComparison.OrdinalIgnoreCase) ||
                 name.Contains("Secret", StringComparison.OrdinalIgnoreCase)) &&
                !name.EndsWith("SecretReference", StringComparison.Ordinal))
            .ToList();

        suspiciousNames.Should().BeEmpty(
            "WhatsAppIntegration must only ever persist opaque secret REFERENCES, never a raw secret value — " +
            $"found suspicious propert{(suspiciousNames.Count == 1 ? "y" : "ies")}: {string.Join(", ", suspiciousNames)}");
    }

    [Fact]
    public void ExternalIntegrationsDbContext_Owns_The_Approved_Schema_Name()
    {
        using var dbContext = new ExternalIntegrationsDbContextFactory().CreateDbContext([]);

        dbContext.SchemaName.Should().Be("external_integrations");
    }

    /// <summary>
    /// WhatsAppIntegration must be tenant-owned (RLS/Global Query Filter
    /// eligible) — CP2.1 mandate §15/§16.
    /// </summary>
    [Fact]
    public void WhatsAppIntegration_Is_Tenant_Owned()
    {
        typeof(ITenantOwned).IsAssignableFrom(typeof(WhatsAppIntegration)).Should().BeTrue(
            "WhatsAppIntegration must implement ITenantOwned for the Global Query Filter + RLS to apply");
    }

    // ---- Fase 9, Checkpoint 2.2 (Meta WhatsApp Outbound Connector) ----------

    /// <summary>
    /// Mandate §45: Communication may know <see cref="IMessagingProvider"/>
    /// exists, but must never know a real provider's own vocabulary —
    /// no Meta/Graph/wamid-named type may appear anywhere in Communication's
    /// three assemblies.
    /// </summary>
    [Fact]
    public void Communication_Assemblies_Contain_No_Meta_Named_Type()
    {
        var assembliesToCheck = new[]
        {
            typeof(IHostPro.Contexts.Communication.Domain.Message).Assembly,
            typeof(IHostPro.Contexts.Communication.Application.IOutboundMessageConnector).Assembly,
            typeof(IHostPro.Contexts.Communication.Infrastructure.Persistence.CommunicationDbContext).Assembly,
        };
        var forbiddenSubstrings = new[] { "Meta", "Graph", "Wamid" };

        foreach (var assembly in assembliesToCheck.Distinct())
        {
            // Scoped to this codebase's own types only — third-party/generated
            // types (e.g. Mediator's own "ContainerMetadata") may coincidentally
            // contain one of these substrings and are not this rule's concern.
            var typeNames = Types.InAssembly(assembly).GetTypes()
                .Select(t => t.FullName ?? t.Name)
                .Where(name => name.StartsWith("IHostPro.", StringComparison.Ordinal))
                .ToList();

            foreach (var forbidden in forbiddenSubstrings)
            {
                typeNames.Should().NotContain(
                    name => name.Contains(forbidden, StringComparison.Ordinal),
                    $"{assembly.GetName().Name} must stay provider-neutral — no type name may reference '{forbidden}'");
            }
        }
    }

    /// <summary>
    /// Mandate §6: every Meta-specific type is confined to
    /// <c>ExternalIntegrations.Infrastructure.Meta</c> — never in
    /// <c>Contracts</c>/<c>Domain</c>/<c>Application</c>/<c>Api</c>.
    /// </summary>
    [Fact]
    public void Meta_Named_Types_Only_Exist_In_The_Infrastructure_Meta_Namespace()
    {
        var assembliesToCheck = new[]
        {
            typeof(IMessagingProvider).Assembly, // Contracts
            typeof(WhatsAppIntegration).Assembly, // Domain
            typeof(IWhatsAppCredentialProvider).Assembly, // Application
            typeof(IHostPro.Contexts.ExternalIntegrations.Api.Controllers.WhatsAppIntegrationController).Assembly, // Api
        };
        var forbiddenSubstrings = new[] { "Meta", "Graph", "Wamid" };

        foreach (var assembly in assembliesToCheck.Distinct())
        {
            var typeNames = Types.InAssembly(assembly).GetTypes()
                .Select(t => t.FullName ?? t.Name)
                .Where(name => name.StartsWith("IHostPro.", StringComparison.Ordinal))
                .ToList();

            foreach (var forbidden in forbiddenSubstrings)
            {
                typeNames.Should().NotContain(
                    name => name.Contains(forbidden, StringComparison.Ordinal),
                    $"{assembly.GetName().Name} must never contain a Meta-named type — those are confined to ExternalIntegrations.Infrastructure.Meta");
            }
        }
    }

    /// <summary>Mandate §12/§13: zero automatic retry — no resilience/Polly package is referenced by Infrastructure.</summary>
    [Fact]
    public void Infrastructure_References_No_Resilience_Or_Polly_Package()
    {
        var referencedAssemblyNames = typeof(ExternalIntegrationsDbContext).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name)
            .Where(name => name is not null)
            .ToList();

        referencedAssemblyNames.Should().NotContain(name =>
            name!.Contains("Polly", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Resilience", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Mandate §16: no entity anywhere in External Integrations declares a raw access-token-shaped property outside the established secret-REFERENCE naming.</summary>
    [Fact]
    public void WhatsAppTemplateMapping_Never_Declares_A_Secret_Property()
    {
        var propertyNames = typeof(WhatsAppTemplateMapping)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToList();

        var suspiciousNames = propertyNames
            .Where(name => name.Contains("Token", StringComparison.OrdinalIgnoreCase) || name.Contains("Secret", StringComparison.OrdinalIgnoreCase))
            .ToList();

        suspiciousNames.Should().BeEmpty("a template mapping carries no credential — never a token/secret-shaped property");
    }

    [Fact]
    public void WhatsAppTemplateMapping_Is_Tenant_Owned()
    {
        typeof(ITenantOwned).IsAssignableFrom(typeof(WhatsAppTemplateMapping)).Should().BeTrue(
            "WhatsAppTemplateMapping must implement ITenantOwned for the Global Query Filter + RLS to apply");
    }

    /// <summary>Mandate §27-33: DeliveryOutcomeUnknown must exist as a ProviderFailureCategory value, never as a new MessageStatus.</summary>
    [Fact]
    public void ProviderFailureCategory_Declares_DeliveryOutcomeUnknown()
    {
        Enum.GetNames<ProviderFailureCategory>().Should().Contain(nameof(ProviderFailureCategory.DeliveryOutcomeUnknown));
    }

    [Fact]
    public void Communication_MessageStatus_Gains_No_New_Value_For_DeliveryOutcomeUnknown()
    {
        var names = Enum.GetNames<IHostPro.Contexts.Communication.Domain.MessageStatus>();

        names.Should().NotContain(n =>
            n.Contains("Unknown", StringComparison.OrdinalIgnoreCase) ||
            n.Contains("Uncertain", StringComparison.OrdinalIgnoreCase) ||
            n.Contains("Reprocess", StringComparison.OrdinalIgnoreCase),
            "an ambiguous outcome maps to the existing Failed status plus a failure code — never a new MessageStatus value (mandate §27-31)");
    }

    // ---- Fase 9, Checkpoint 2.3.1 (WhatsApp Webhook Security Ingress) ------

    /// <summary>
    /// ADR-022, item 9: the webhook's own security types (controller,
    /// signature verifier, app-level credential provider) must never
    /// reference the tenant-owned credential path
    /// (<see cref="IWhatsAppCredentialProvider"/>/<c>WhatsAppIntegration</c>/
    /// <c>IWhatsAppIntegrationRepository</c>) — the webhook verifies its
    /// caller before any <c>TenantId</c> is known, so it structurally cannot
    /// resolve a tenant's own integration.
    /// </summary>
    [Fact]
    public void WhatsAppWebhookController_Never_References_The_Tenant_Owned_Credential_Path()
    {
        var result = Types.InAssembly(typeof(IHostPro.Contexts.ExternalIntegrations.Api.Controllers.WhatsAppWebhookController).Assembly)
            .That()
            .HaveNameEndingWith("WebhookController")
            .Should()
            .NotHaveDependencyOnAny(
                "IHostPro.Contexts.ExternalIntegrations.Application.IWhatsAppCredentialProvider",
                "IHostPro.Contexts.ExternalIntegrations.Application.WhatsAppIntegrations",
                "IHostPro.Contexts.ExternalIntegrations.Domain")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            "the webhook must verify its caller before any TenantId is known — it can never resolve credentials via " +
            "the tenant-owned WhatsAppIntegration/IWhatsAppCredentialProvider path (ADR-022, item 9). " +
            BuildFailureMessage(result));
    }

    /// <summary>
    /// CP2.3.1 mandate §29/§30: this checkpoint touches no persistence and no
    /// messaging at all — the webhook controller/its security dependencies
    /// must have zero compile-time dependency on EF Core or Wolverine.
    /// </summary>
    [Fact]
    public void Webhook_Security_Ingress_Has_No_Dependency_On_EfCore_Or_Wolverine()
    {
        var result = Types.InAssembly(typeof(IHostPro.Contexts.ExternalIntegrations.Api.Controllers.WhatsAppWebhookController).Assembly)
            .That()
            .HaveNameEndingWith("WebhookController")
            .Should()
            .NotHaveDependencyOnAny("Microsoft.EntityFrameworkCore", "WolverineFx")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(BuildFailureMessage(result));
    }

    private static string BuildFailureMessage(TestResult result) =>
        result.FailingTypes is null
            ? "Architecture rule violated."
            : "Architecture rule violated by: " + string.Join(", ", result.FailingTypes.Select(t => t.FullName));
}
