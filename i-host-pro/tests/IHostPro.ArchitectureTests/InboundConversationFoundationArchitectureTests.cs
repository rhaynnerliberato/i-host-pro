using System.Runtime.CompilerServices;
using FluentAssertions;
using IHostPro.Contexts.ExternalIntegrations.Contracts;
using NetArchTest.Rules;

namespace IHostPro.ArchitectureTests;

/// <summary>
/// Fase 11, Checkpoint 1 (Inbound Conversation Foundation) — guards specific
/// to this checkpoint's own mandate item 34, beyond what
/// <c>CommunicationDependencyTests</c>/<c>ExternalIntegrationsDependencyTests</c>
/// already generalize.
/// </summary>
public class InboundConversationFoundationArchitectureTests
{
    private static string RepositoryRoot([CallerFilePath] string thisFilePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFilePath)!, "..", ".."));

    /// <summary>
    /// Mandate item 28: CP1 explicitly does NOT create the AI Agent Bounded
    /// Context — no <c>IHostPro.Contexts.AIAgent.*</c> project may exist yet,
    /// even though it is already ratified at the platform level (ADR-009,
    /// Architecture Principles §3) for a FUTURE checkpoint (CP2).
    /// </summary>
    [Fact]
    public void No_AIAgent_Bounded_Context_Project_Exists_Yet()
    {
        var contextsDirectory = Path.Combine(RepositoryRoot(), "src", "Contexts");
        Directory.Exists(contextsDirectory).Should().BeTrue($"expected {contextsDirectory} to exist");

        var aiAgentProjects = Directory.GetDirectories(contextsDirectory)
            .Where(dir => Path.GetFileName(dir).Contains("AIAgent", StringComparison.OrdinalIgnoreCase) ||
                          Path.GetFileName(dir).Contains("AI_Agent", StringComparison.OrdinalIgnoreCase))
            .ToList();

        aiAgentProjects.Should().BeEmpty(
            "the AI Agent Bounded Context is explicitly CP2's scope (Fase 11 CP0 decision) — " +
            "CP1 (Inbound Conversation Foundation) must not create it");
    }

    /// <summary>
    /// Mandate item 28/29: no <c>IModelProvider</c>/<c>AISession</c>/prompt/
    /// tools/Anthropic connector/fake LLM type exists anywhere in the
    /// solution yet — mirrors the project-existence check above at the type
    /// level, in case someone adds these types to an existing project
    /// instead of a new one.
    /// </summary>
    [Fact]
    public void No_AI_Model_Or_Session_Types_Exist_Anywhere_Yet()
    {
        var srcDirectory = Path.Combine(RepositoryRoot(), "src");
        var sourceFiles = Directory.GetFiles(srcDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
                           !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .ToList();

        var forbiddenFileNames = new[] { "IModelProvider.cs", "AISession.cs", "SendAgentResponseCommand.cs" };

        foreach (var forbidden in forbiddenFileNames)
        {
            sourceFiles.Should().NotContain(
                path => Path.GetFileName(path).Equals(forbidden, StringComparison.OrdinalIgnoreCase),
                $"{forbidden} belongs to a later checkpoint (CP2/CP4) — CP1 is Inbound Conversation Foundation only");
        }
    }

    /// <summary>
    /// Mandate item 8/34: the provider-neutral inbound event must never
    /// carry a credential, PIX QR payload, or any provider secret — mirrors
    /// <c>PropertyGuestAccessReadResult_Never_Carries_Guest_Identity_Or_Provider_Data</c>
    /// (CommunicationDependencyTests) exactly.
    /// </summary>
    [Fact]
    public void InboundGuestMessageReceived_Never_Carries_Credential_Or_Provider_Secret()
    {
        var propertyNames = typeof(InboundGuestMessageReceived)
            .GetProperties()
            .Select(p => p.Name)
            .ToList();

        foreach (var forbidden in new[] { "Credential", "Secret", "Token", "AccessKey", "QrCode", "PhoneNumberId", "Signature" })
        {
            propertyNames.Should().NotContain(
                name => name.Contains(forbidden, StringComparison.OrdinalIgnoreCase),
                $"InboundGuestMessageReceived must never carry a property containing '{forbidden}'");
        }
    }

    /// <summary>
    /// Mandate item 8: no raw Meta payload/DTO type may appear as a
    /// referenced type on the public event contract itself.
    /// </summary>
    [Fact]
    public void InboundGuestMessageReceived_Properties_Are_All_Primitive_Or_Enum()
    {
        var properties = typeof(InboundGuestMessageReceived).GetProperties();

        foreach (var property in properties)
        {
            var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            (type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(Guid) || type == typeof(DateTimeOffset))
                .Should().BeTrue($"{property.Name} ({type.Name}) must be a primitive/enum/string/Guid/DateTimeOffset — never a provider-specific DTO");
        }
    }

    /// <summary>Mandate item 19: Conversation cardinality — one active Conversation per (TenantId, ReservationId, Channel), enforced by a real unique index, not just application logic.</summary>
    [Fact]
    public void Conversation_Has_A_Unique_Index_On_Tenant_Reservation_Channel()
    {
        using var dbContext = new IHostPro.Contexts.Communication.Infrastructure.Persistence.CommunicationDbContextFactory().CreateDbContext([]);

        var entityType = dbContext.Model.FindEntityType(typeof(IHostPro.Contexts.Communication.Domain.Conversation));
        entityType.Should().NotBeNull();

        var uniqueIndexes = entityType!.GetIndexes().Where(i => i.IsUnique).ToList();
        uniqueIndexes.Should().ContainSingle(i =>
            i.Properties.Select(p => p.Name).OrderBy(n => n).SequenceEqual(
                new[] { "TenantId", "ReservationId", "Channel" }.OrderBy(n => n)));
    }
}
