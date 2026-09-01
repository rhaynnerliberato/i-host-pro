using System.Reflection;
using System.Xml.Linq;
using FluentAssertions;
using IHostPro.Contexts.AIAgent.Application;
using IHostPro.Contexts.AIAgent.Domain;
using IHostPro.Contexts.AIAgent.Infrastructure.ModelProviders.Anthropic;
using IHostPro.Contexts.Configuration.Contracts;

namespace IHostPro.ArchitectureTests;

/// <summary>
/// Fase 11, Checkpoint 7 (Anthropic Claude Real Proof). Proves: every
/// Anthropic-specific type lives exclusively in AIAgent.Infrastructure's own
/// dedicated namespace (mandate item 6); no third-party Anthropic SDK
/// package is referenced anywhere (ADR-009 — REST/HttpClient only); the API
/// key never becomes a field on any type outside the credential provider
/// itself (mandate item 7); <see cref="AiAgentBehaviorPolicy"/> never carries
/// a <c>Temperature</c> field (the selected model, <c>claude-sonnet-4-6</c>,
/// rejects any custom value — CP7's own governance record, mandate item
/// 12/13); the Context Builder's own cross-context surface never touches a
/// credential/secret/QR-shaped member (mandate item 62).
/// </summary>
public class AnthropicModelProviderArchitectureTests
{
    [Fact]
    public void Every_Anthropic_Specific_Type_Lives_In_Its_Own_Dedicated_Infrastructure_Namespace()
    {
        const string expectedNamespace = "IHostPro.Contexts.AIAgent.Infrastructure.ModelProviders.Anthropic";

        var offenders = typeof(AnthropicModelProvider).Assembly.GetTypes()
            .Where(t => (t.FullName ?? t.Name).Contains("Anthropic", StringComparison.OrdinalIgnoreCase)
                        || (t.FullName ?? t.Name).Contains("Claude", StringComparison.OrdinalIgnoreCase))
            .Where(t => t.Namespace != expectedNamespace)
            .Select(t => t.FullName)
            .ToList();

        offenders.Should().BeEmpty(
            $"every Anthropic-specific type must live in {expectedNamespace} — never scattered elsewhere in Infrastructure. Found: " +
            string.Join(", ", offenders));
    }

    [Fact]
    public void No_Third_Party_Anthropic_SDK_Package_Is_Referenced()
    {
        var csprojPath = Path.Combine(
            RepositoryRoot(), "src", "Contexts", "AIAgent", "IHostPro.Contexts.AIAgent.Infrastructure",
            "IHostPro.Contexts.AIAgent.Infrastructure.csproj");

        File.Exists(csprojPath).Should().BeTrue($"expected {csprojPath} to exist");

        var packageReferences = XDocument.Load(csprojPath)
            .Descendants("PackageReference")
            .Select(e => e.Attribute("Include")?.Value ?? string.Empty)
            .ToList();

        packageReferences.Should().NotContain(
            name => name.Contains("Anthropic", StringComparison.OrdinalIgnoreCase),
            "ADR-009 requires raw REST via IHttpClientFactory — no official or unofficial Anthropic SDK package");
    }

    [Fact]
    public void No_Api_Key_Field_Exists_Outside_The_Credential_Provider_Itself()
    {
        var offenders = typeof(AnthropicModelProvider).Assembly.GetTypes()
            .Where(t => t != typeof(DevelopmentAnthropicCredentialProvider))
            // Excludes compiler-generated async state machines (e.g.
            // AnthropicModelProvider's own `d__NN` nested type) — an
            // `apiKey` local variable hoisted there for the lifetime of one
            // `await` is not a stored field, unlike a genuine type member.
            .Where(t => t.GetCustomAttribute<System.Runtime.CompilerServices.CompilerGeneratedAttribute>() is null)
            .Where(t => t.IsClass || t.IsInterface)
            .SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Select(p => (Type: t, Member: p.Name))
                .Concat(t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                    .Select(f => (Type: t, Member: f.Name))))
            .Where(x => x.Member.Contains("ApiKey", StringComparison.OrdinalIgnoreCase))
            .Select(x => $"{x.Type.FullName}.{x.Member}")
            .ToList();

        offenders.Should().BeEmpty(
            "the Anthropic API key must never be stored as a field on any type other than the credential provider itself. Found: " +
            string.Join(", ", offenders));
    }

    [Fact]
    public void AiAgentBehaviorPolicy_Never_Carries_A_Temperature_Field()
    {
        var members = typeof(AiAgentBehaviorPolicy)
            .GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToList();

        members.Should().NotContain(
            name => name.Contains("Temperature", StringComparison.OrdinalIgnoreCase),
            "claude-sonnet-4-6 rejects any custom temperature value with HTTP 400 (CP7 governance record) — " +
            "the field was deliberately removed from the MVP contract, never reintroduced to preserve an old requirement");
    }

    [Fact]
    public void AgentContextBuilder_Never_Depends_On_A_Credential_Secret_Or_QR_Shaped_Type()
    {
        var constructorParameterTypes = typeof(AgentContextBuilder)
            .GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .SelectMany(c => c.GetParameters())
            .Select(p => p.ParameterType)
            .ToList();

        var suspiciousNames = new[] { "Credential", "SecretReference", "QrCode", "AccessCredential" };

        var offenders = constructorParameterTypes
            .Where(t => suspiciousNames.Any(name => t.Name.Contains(name, StringComparison.OrdinalIgnoreCase)))
            .Select(t => t.FullName)
            .ToList();

        offenders.Should().BeEmpty(
            "the Context Builder's own cross-context surface must never touch a credential/secret/QR-shaped type " +
            "(mandate item 62). Found: " + string.Join(", ", offenders));
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "IHostPro.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Could not locate IHostPro.sln.");
    }
}
