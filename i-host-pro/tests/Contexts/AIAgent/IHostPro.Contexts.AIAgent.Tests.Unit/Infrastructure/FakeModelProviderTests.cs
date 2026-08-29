using FluentAssertions;
using IHostPro.Contexts.AIAgent.Application;
using IHostPro.Contexts.AIAgent.Infrastructure.ModelProviders;
using Microsoft.Extensions.Logging.Abstractions;

namespace IHostPro.Contexts.AIAgent.Tests.Unit.Infrastructure;

/// <summary>Fase 11, Checkpoint 2 (AI Agent Foundation) — mandate item 45: deterministic response, controlled failure, token metadata.</summary>
public class FakeModelProviderTests
{
    private static FakeModelProvider CreateProvider() => new(NullLogger<FakeModelProvider>.Instance);

    [Fact]
    public async Task GenerateAsync_returns_a_deterministic_response_for_the_same_input()
    {
        var provider = CreateProvider();
        var request = new ModelRequest(SystemPrompt: null, Messages: [new ModelMessage(ModelMessageRole.Guest, "Olá, preciso de ajuda")]);

        var first = await provider.GenerateAsync(request, CancellationToken.None);
        var second = await provider.GenerateAsync(request, CancellationToken.None);

        first.Should().BeEquivalentTo(second);
    }

    [Fact]
    public async Task GenerateAsync_never_leaks_provider_specific_stop_reason_modeling()
    {
        var provider = CreateProvider();
        var request = new ModelRequest(SystemPrompt: null, Messages: [new ModelMessage(ModelMessageRole.Guest, "Olá")]);

        var result = await provider.GenerateAsync(request, CancellationToken.None);

        result.ModelName.Should().Be("fake-model-v1");
        result.DetectedLanguage.Should().Be("pt-BR");
        result.Intent.Should().BeNull();
        result.Confidence.Should().BeNull("default behavior — no confidence unless a test explicitly configures the marker (governance resolution item 9)");
    }

    [Fact]
    public async Task GenerateAsync_returns_the_configured_confidence_when_the_marker_is_present()
    {
        var provider = CreateProvider();
        var request = new ModelRequest(
            SystemPrompt: null,
            Messages: [new ModelMessage(ModelMessageRole.Guest, $"Olá {FakeModelProvider.ConfidenceMarkerPrefix}0.90]")]);

        var result = await provider.GenerateAsync(request, CancellationToken.None);

        result.Confidence.Should().Be(0.90m);
    }

    [Fact]
    public async Task GenerateAsync_computes_fake_but_explicit_token_metadata_from_input()
    {
        var provider = CreateProvider();
        var request = new ModelRequest(SystemPrompt: null, Messages: [new ModelMessage(ModelMessageRole.Guest, "0123456789")]);

        var result = await provider.GenerateAsync(request, CancellationToken.None);

        result.InputTokens.Should().Be(10);
        result.OutputTokens.Should().Be(result.Text.Length);
    }

    [Fact]
    public async Task GenerateAsync_throws_ModelProviderException_when_the_failure_trigger_marker_is_present()
    {
        var provider = CreateProvider();
        var request = new ModelRequest(
            SystemPrompt: null,
            Messages: [new ModelMessage(ModelMessageRole.Guest, $"anything {FakeModelProvider.FailureTriggerMarker}")]);

        var act = async () => await provider.GenerateAsync(request, CancellationToken.None);

        await act.Should().ThrowAsync<ModelProviderException>();
    }
}
